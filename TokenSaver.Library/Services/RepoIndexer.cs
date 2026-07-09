using TokenSaver.Library.Configuration;
using TokenSaver.Library.Contracts;
using TokenSaver.Database.Context;
using TokenSaver.Database.Context.Entities;
using TokenSaver.Library.Models.TokenEconomy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TokenSaver.Library.Services;

/// <summary>Default <see cref="IRepoIndexer" /> — see that interface's doc comment.</summary>
public sealed class RepoIndexer(
    IChunkExtractor chunkExtractor,
    OllamaService ollama,
    ModelRouteResolver modelRoutes,
    TokenEconomyDbAccessor db,
    IOptions<TokenSaverConfig> config)
    : IRepoIndexer
{
    private static readonly string[] DefaultExtensions = [".cs", ".js", ".ts", ".py", ".md"];

    private static readonly string[] IgnoredSegments =
        ["node_modules", ".git", "dist", "build", "venv", "__pycache__", "bin", "obj", ".tokensaver", ".vs"];

    private readonly TokenEconomyConfig _config = config.Value.TokenEconomy;

    public async Task<RepoIndexStats> IndexRepoAsync(
        string workspaceRoot, string[]? extensions = null, int concurrency = 4, CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        HashSet<string> exts = new(extensions ?? DefaultExtensions, StringComparer.OrdinalIgnoreCase);

        EnumerationOptions enumOptions = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };
        List<string> files = Directory.EnumerateFiles(workspaceRoot, "*", enumOptions)
            .Where(f => exts.Contains(Path.GetExtension(f)) && !IsIgnored(workspaceRoot, f))
            .ToList();

        WorkspaceIndexContext ctx = await db.GetDbAsync(workspaceRoot, ct);

        // Existing chunk hashes, keyed by (RelativePath, StartLine, EndLine) — loaded once up
        // front so per-chunk unchanged-checks don't each hit the DB.
        Dictionary<(string Path, int Start, int End), string> existingHashes = await db.WithDbLockAsync(
            () => ctx.Chunks.Select(c => new { c.RelativePath, c.StartLine, c.EndLine, c.ContentHash })
                .ToDictionaryAsync(c => (c.RelativePath, c.StartLine, c.EndLine), c => c.ContentHash, ct),
            ct);

        int indexed = 0, skipped = 0, deleted = 0;
        using SemaphoreSlim ollamaLimit = new(Math.Max(1, concurrency));
        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);

        foreach (string filePath in files)
        {
            ct.ThrowIfCancellationRequested();
            string relativePath = Path.GetRelativePath(workspaceRoot, filePath).Replace('\\', '/');
            seenPaths.Add(relativePath);

            string content;
            try
            {
                content = await File.ReadAllTextAsync(filePath, ct);
            }
            catch (IOException)
            {
                continue; // locked/transient — skip this pass, next run picks it up
            }

            IReadOnlyList<CodeChunk> chunks = chunkExtractor.ExtractChunks(filePath, content);
            HashSet<(int Start, int End)> seenSpans = new();

            IEnumerable<Task> chunkTasks = chunks.Select(async chunk =>
            {
                seenSpans.Add((chunk.StartLine, chunk.EndLine));
                string hash = ComputeHash(chunk.Text);
                bool unchanged = existingHashes.TryGetValue((relativePath, chunk.StartLine, chunk.EndLine), out string? existingHash)
                                  && existingHash == hash;
                if (unchanged)
                {
                    _ = Interlocked.Increment(ref skipped);
                    return;
                }

                await ollamaLimit.WaitAsync(ct);
                try
                {
                    string summary = await SummarizeChunkAsync(chunk, ct);
                    string embedInput = _config.EnableSummaryEmbedding ? summary : chunk.Text;
                    float[] vector = await ollama.EmbedAsync(modelRoutes.Resolve("embed"), embedInput, ct: ct);

                    await db.WithDbLockAsync(async () =>
                    {
                        ChunkEntity? row = await ctx.Chunks.FirstOrDefaultAsync(
                            c => c.RelativePath == relativePath && c.StartLine == chunk.StartLine && c.EndLine == chunk.EndLine, ct);
                        if (row is null)
                        {
                            row = new ChunkEntity { RelativePath = relativePath, StartLine = chunk.StartLine, EndLine = chunk.EndLine };
                            _ = ctx.Chunks.Add(row);
                        }

                        row.ChunkType = chunk.ChunkType;
                        row.Name = chunk.Name;
                        row.ContentHash = hash;
                        row.Summary = summary;
                        row.EmbeddingVector = JsonSerializer.Serialize(vector);
                        row.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        _ = await ctx.SaveChangesAsync(ct);
                    }, ct);

                    _ = Interlocked.Increment(ref indexed);
                }
                finally
                {
                    _ = ollamaLimit.Release();
                }
            });

            await Task.WhenAll(chunkTasks);

            // Prune chunks for this file whose span no longer exists (function deleted/renamed).
            int prunedForFile = await db.WithDbLockAsync(async () =>
            {
                List<ChunkEntity> rows = await ctx.Chunks.Where(c => c.RelativePath == relativePath).ToListAsync(ct);
                List<ChunkEntity> stale = rows.Where(r => !seenSpans.Contains((r.StartLine, r.EndLine))).ToList();
                ctx.Chunks.RemoveRange(stale);
                _ = await ctx.SaveChangesAsync(ct);
                return stale.Count;
            }, ct);
            deleted += prunedForFile;
        }

        // Prune chunks for files that no longer exist on disk at all.
        int prunedMissingFiles = await db.WithDbLockAsync(async () =>
        {
            List<string> dbPaths = await ctx.Chunks.Select(c => c.RelativePath).Distinct().ToListAsync(ct);
            List<string> gonePaths = dbPaths.Where(p => !seenPaths.Contains(p)).ToList();
            if (gonePaths.Count == 0)
            {
                return 0;
            }

            List<ChunkEntity> rows = await ctx.Chunks.Where(c => gonePaths.Contains(c.RelativePath)).ToListAsync(ct);
            ctx.Chunks.RemoveRange(rows);
            _ = await ctx.SaveChangesAsync(ct);
            return rows.Count;
        }, ct);
        deleted += prunedMissingFiles;

        sw.Stop();
        return new RepoIndexStats(files.Count, indexed, skipped, deleted, sw.Elapsed);
    }

    private async Task<string> SummarizeChunkAsync(CodeChunk chunk, CancellationToken ct)
    {
        string text = chunk.Text.Length > 2000 ? chunk.Text[..2000] : chunk.Text;
        string prompt = chunk.ChunkType == "md_section"
            ? $"One line only. Describe what this documentation section covers. Section heading: \"{chunk.Name}\".\n\n{text}"
            : $"One line only, no preamble. Describe what this function/class does and its key inputs/outputs.\n\nCODE ({chunk.Name}):\n{text}";

        return await ollama.CallModelAsync(modelRoutes.Resolve("summarize"), prompt, ct);
    }

    private static bool IsIgnored(string workspaceRoot, string filePath)
    {
        string relative = Path.GetRelativePath(workspaceRoot, filePath);
        string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s => IgnoredSegments.Contains(s, StringComparer.OrdinalIgnoreCase));
    }

    private static string ComputeHash(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
