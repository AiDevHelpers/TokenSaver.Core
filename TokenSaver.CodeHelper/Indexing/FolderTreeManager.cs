using TokenSaver.Config;
using TokenSaver.Database.Context;
using TokenSaver.Database.Context.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TokenSaver.CodeHelper.Indexing;

/// <summary>
///     Owns the Folder tree: bootstrap creation, incremental ancestor-chain creation for
///     single-file updates, description aggregation, and on-demand folder-index JSON retrieval
///     (replaces the old LocalContextService folder_indices JSON blob).
/// </summary>
public sealed class FolderTreeManager
{
    private static readonly HashSet<string> AlwaysSkipDirs =
        new(StringComparer.OrdinalIgnoreCase) { ".git", ".tokensaver", "node_modules", "bin", "obj", "dist", "out" };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly WorkspaceIndexDbAccessor _accessor;
    private readonly WorkspaceConfigManager _config;
    private readonly ILogger<FolderTreeManager> _logger;

    public FolderTreeManager(WorkspaceIndexDbAccessor accessor, WorkspaceConfigManager config, ILogger<FolderTreeManager> logger)
    {
        _accessor = accessor;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    ///     Full-tree BFS: ensures every non-excluded directory has a Folder row. Only
    ///     used on a true bootstrap (no git baseline anywhere, or the very first index run).
    /// </summary>
    public async Task<Dictionary<string, int>> EnsureFoldersAsync(WorkspaceIndexContext ctx, string root,
        CancellationToken ct)
    {
        WorkspaceIgnoreFilter ignore = WorkspaceIgnoreFilter.Load(root);
        Dictionary<string, int> folderIds = new(StringComparer.OrdinalIgnoreCase);

        await _accessor.WithDbLockAsync(async () =>
        {
            int rootId = await EnsureFolderRowAsync(ctx, ".", null, ct);
            folderIds["."] = rootId;

            Queue<(string Dir, string Rel, int ParentId)> queue = new();
            queue.Enqueue((root, ".", rootId));

            while (queue.Count > 0)
            {
                (string? dir, string? relPath, int parentId) = queue.Dequeue();
                IEnumerable<string> subdirs;
                try
                {
                    subdirs = Directory.EnumerateDirectories(dir);
                }
                catch
                {
                    continue;
                }

                foreach (string sub in subdirs)
                {
                    string name = Path.GetFileName(sub);
                    if (AlwaysSkipDirs.Contains(name))
                    {
                        continue;
                    }

                    string subRel = relPath == "." ? name : $"{relPath}/{name}";
                    if (ignore.IsIgnored(subRel, true))
                    {
                        continue;
                    }

                    int subId = await EnsureFolderRowAsync(ctx, subRel, parentId, ct);
                    folderIds[subRel] = subId;
                    queue.Enqueue((sub, subRel, subId));
                }
            }
        }, ct);

        return folderIds;
    }

    public async Task<int> GetOrCreateFolderIdAsync(
        WorkspaceIndexContext ctx, string folderRel, Dictionary<string, int>? prebuilt, CancellationToken ct)
    {
        return prebuilt != null && prebuilt.TryGetValue(folderRel, out int id)
            ? id
            : await _accessor.WithDbLockAsync(() => EnsureFolderChainAsync(ctx, folderRel, ct), ct);
    }

    /// <summary>
    ///     Ensures the ancestor chain root..folderRel exists, creating only what's missing.
    ///     Cheap for incremental single-file updates (proportional to path depth, not tree size).
    /// </summary>
    private static async Task<int> EnsureFolderChainAsync(WorkspaceIndexContext ctx, string folderRel,
        CancellationToken ct)
    {
        string[] segments = folderRel == "." ? [] : folderRel.Split('/');
        string currentRel = ".";
        int parentId = await EnsureFolderRowAsync(ctx, ".", null, ct);

        foreach (string seg in segments)
        {
            currentRel = currentRel == "." ? seg : $"{currentRel}/{seg}";
            parentId = await EnsureFolderRowAsync(ctx, currentRel, parentId, ct);
        }

        return parentId;
    }

    private static async Task<int> EnsureFolderRowAsync(WorkspaceIndexContext ctx, string relPath, int? parentId,
        CancellationToken ct)
    {
        FolderEntity? row = await ctx.Folders.FirstOrDefaultAsync(f => f.RelativePath == relPath, ct);
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (row == null)
        {
            row = new FolderEntity
            {
                RelativePath = relPath,
                ParentFolderId = parentId,
                Description = "",
                LastModified = nowMs,
                UpdatedAt = nowMs
            };
            _ = ctx.Folders.Add(row);
            _ = await ctx.SaveChangesAsync(ct);
        }
        else if (row.ParentFolderId != parentId)
        {
            row.ParentFolderId = parentId;
            _ = await ctx.SaveChangesAsync(ct);
        }

        return row.Id;
    }

    public static string GetParentFolderRelPath(string relPath)
    {
        int idx = relPath.LastIndexOf('/');
        return idx < 0 ? "." : relPath[..idx];
    }

    /// <summary>
    ///     Regenerates the Description for every folder touched by this index pass, plus
    ///     all their ancestors up to the root, from a cheap static aggregate of immediate children
    ///     (no extra AI calls — file-level descriptions already carry the detail).
    /// </summary>
    public async Task RefreshTouchedFolderDescriptionsAsync(WorkspaceIndexContext ctx, IReadOnlyList<string> touchedFiles,
        CancellationToken ct)
    {
        HashSet<string> foldersToRefresh = new(StringComparer.OrdinalIgnoreCase);
        foreach (string f in touchedFiles)
        {
            string folder = GetParentFolderRelPath(f);
            while (true)
            {
                _ = foldersToRefresh.Add(folder);
                if (folder == ".")
                {
                    break;
                }

                folder = GetParentFolderRelPath(folder);
            }
        }

        await _accessor.WithDbLockAsync(async () =>
        {
            foreach (string folderRel in foldersToRefresh)
            {
                FolderEntity? folderRow = await ctx.Folders.FirstOrDefaultAsync(f => f.RelativePath == folderRel, ct);
                if (folderRow == null)
                {
                    continue;
                }

                List<FileEntity> childFiles = await ctx.Files.Where(f => f.FolderId == folderRow.Id).ToListAsync(ct);
                int childFolderCount = await ctx.Folders.CountAsync(f => f.ParentFolderId == folderRow.Id, ct);

                folderRow.Description = BuildFolderDescription(childFolderCount, childFiles);
                folderRow.LastModified =
                    childFiles.Count > 0 ? childFiles.Max(f => f.LastModified) : folderRow.LastModified;
                folderRow.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            _ = await ctx.SaveChangesAsync(ct);
        }, ct);
    }

    private static string BuildFolderDescription(int childFolderCount, List<FileEntity> files)
    {
        if (files.Count == 0 && childFolderCount == 0)
        {
            return "empty folder";
        }

        List<string> byType = files.GroupBy(f => f.FileType)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {g.Key}")
            .ToList();

        StringBuilder sb = new();
        if (childFolderCount > 0)
        {
            _ = sb.Append(CultureInfo.InvariantCulture, $"{childFolderCount} subfolder(s), ");
        }

        _ = sb.Append(files.Count > 0 ? $"{files.Count} file(s)" : "no files");
        if (byType.Count > 0)
        {
            _ = sb.Append(CultureInfo.InvariantCulture, $" ({string.Join(", ", byType.Take(6))})");
        }

        List<string> notable = files
            .Where(f => !string.IsNullOrWhiteSpace(f.Description) && f.Description != Path.GetFileName(f.RelativePath))
            .OrderByDescending(f => f.LastModified)
            .Take(5)
            .Select(f => $"{Path.GetFileName(f.RelativePath)}: {f.Description}")
            .ToList();
        if (notable.Count > 0)
        {
            _ = sb.Append(". Notable: " + string.Join("; ", notable));
        }

        return sb.ToString();
    }

    public async Task<string?> GetFolderIndexJsonAsync(string? path = null, string? workspaceRoot = null,
        CancellationToken ct = default)
    {
        string root = workspaceRoot ?? _config.GetWorkspaceRoot();
        WorkspaceIndexContext ctx = await _accessor.GetDbAsync(root, ct);
        string indexPath = string.IsNullOrWhiteSpace(path) ? "." : path.Replace('\\', '/').TrimEnd('/');

        return await _accessor.WithDbLockAsync(async () =>
        {
            FolderEntity? folder = await ctx.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.RelativePath == indexPath, ct);
            if (folder == null)
            {
                return null;
            }

            var childFolders = await ctx.Folders.AsNoTracking()
                .Where(f => f.ParentFolderId == folder.Id)
                .OrderBy(f => f.RelativePath)
                .Select(f => new { relativePath = f.RelativePath, description = f.Description })
                .ToListAsync(ct);

            var childFiles = await ctx.Files.AsNoTracking()
                .Where(f => f.FolderId == folder.Id)
                .OrderBy(f => f.RelativePath)
                .Select(f => new { relativePath = f.RelativePath, fileType = f.FileType, explanation = f.Description })
                .ToListAsync(ct);

            var index = new
            {
                path = indexPath,
                workspaceRoot = indexPath == "." ? root.Replace('\\', '/') : null,
                description = folder.Description,
                folders = childFolders.Select(f => new
                { name = LastSegment(f.relativePath), f.relativePath, f.description }),
                files = childFiles.Select(f => new
                { name = LastSegment(f.relativePath), f.relativePath, f.fileType, f.explanation }),
                updated = folder.UpdatedAt,
                note = "Call ContextDiag(action='index', id='<path>') to fetch a sub-folder index."
            };

            return JsonSerializer.Serialize(index, JsonOpts);
        }, ct);
    }

    private static string LastSegment(string relPath)
    {
        int idx = relPath.LastIndexOf('/');
        return idx < 0 ? relPath : relPath[(idx + 1)..];
    }

    public static IEnumerable<string> SearchFilesByName(string root, string filename)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        Queue<string> queue = new();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            string dir = queue.Dequeue();
            if (AlwaysSkipDirs.Contains(Path.GetFileName(dir)))
            {
                continue;
            }

            IEnumerable<string> files, dirs;
            try
            {
                files = Directory.EnumerateFiles(dir);
                dirs = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (string file in files)
            {
                if (Path.GetFileName(file).Equals(filename, StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }

            foreach (string sub in dirs)
            {
                queue.Enqueue(sub);
            }
        }
    }
}
