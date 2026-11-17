using TokenSaver.CodeHelper.Configuration;
using TokenSaver.CodeHelper.Indexing;
using TokenSaver.CodeHelper.Models.Patch;
using TokenSaver.Utilities.Services;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TokenSaver.CodeHelper.Patching;

/// <summary>
///     Applies structured, validated code edits with automatic backup + rollback.
///     Each write is atomic: validate → backup → apply → re-index.
/// </summary>
public sealed partial class PatchEngine
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write backup for {File} — aborting")]
    private static partial void LogBackupWriteFailed(ILogger logger, Exception ex, string file);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Re-index after patch failed: {Msg}")]
    private static partial void LogReindexAfterPatchFailed(ILogger logger, string msg);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Op} applied to {File}: {N} lines changed")]
    private static partial void LogPatchApplied(ILogger logger, string op, string file, int n);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rolled back {File} from {Backup}")]
    private static partial void LogRolledBack(ILogger logger, string file, string backup);

    [LoggerMessage(Level = LogLevel.Error, Message = "Rollback failed for {File}")]
    private static partial void LogRollbackFailed(ILogger logger, Exception ex, string file);

    private readonly CodeHelperConfig _config;
    private readonly IFileReindexer _index;
    private readonly ILogger<PatchEngine> _logger;

    public PatchEngine(
        IOptions<CodeHelperConfig> config,
        IFileReindexer index,
        ILogger<PatchEngine> logger)
    {
        _config = config.Value;
        _index = index;
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<PatchResult> ApplyAsync(PatchOperation op, string workspaceRoot, CancellationToken ct = default)
    {
        PathCheck target = WorkspacePathGuard.Resolve(op.FilePath, workspaceRoot);
        if (!target.Ok)
        {
            return Fail(op, target.Error!);
        }

        string abs = target.FullPath;

        if (!File.Exists(abs))
        {
            return Fail(op, $"File not found: {op.FilePath}");
        }

        string content;
        Encoding encoding;
        try
        {
            byte[] raw = await File.ReadAllBytesAsync(abs, ct);
            encoding = DetectEncoding(raw);
            content = encoding.GetString(raw, GetPreambleLength(raw), raw.Length - GetPreambleLength(raw));
        }
        catch (Exception ex)
        {
            return Fail(op, $"Could not read file: {ex.Message}");
        }

        // Validate
        (string? newContent, int linesChanged, string? error) = op switch
        {
            ReplaceOperation r => ApplyReplace(content, r),
            InsertOperation i => ApplyInsert(content, i),
            DeleteOperation d => ApplyDelete(content, d),
            RenameOperation n => ApplyRename(content, n),
            _ => (content, 0, $"Unknown operation type: {op.GetType().Name}")
        };

        if (error != null)
        {
            return Fail(op, error);
        }

        if (op.DryRun)
        {
            return new PatchResult
            {
                Success = true,
                FilePath = op.FilePath,
                LinesChanged = linesChanged,
                WasDryRun = true,
                Description = op.Description
            };
        }

        // Backup
        string? backupPath;
        try
        {
            backupPath = await WriteBackupAsync(abs, op.FilePath, workspaceRoot, ct);
        }
        catch (Exception ex)
        {
            LogBackupWriteFailed(_logger, ex, op.FilePath);
            return Fail(op, $"Backup failed: {ex.Message}");
        }

        // Write
        try
        {
            await WriteAtomicAsync(abs, newContent, encoding, ct);
        }
        catch (Exception ex)
        {
            return Fail(op, $"Write failed: {ex.Message}", backupPath);
        }

        // Re-index
        try
        {
            _ = await _index.IndexFileAsync(workspaceRoot, op.FilePath, ct);
        }
        catch (Exception ex)
        {
            LogReindexAfterPatchFailed(_logger, ex.Message);
        }

        LogPatchApplied(_logger, op.GetType().Name, op.FilePath, linesChanged);

        return new PatchResult
        {
            Success = true,
            FilePath = op.FilePath,
            LinesChanged = linesChanged,
            BackupPath = backupPath,
            Description = op.Description
        };
    }

    public async Task<IReadOnlyList<PatchResult>> ApplyBatchAsync(
        IEnumerable<PatchOperation> ops, string workspaceRoot, CancellationToken ct = default)
    {
        List<PatchResult> results = [];
        foreach (PatchOperation op in ops)
        {
            ct.ThrowIfCancellationRequested();
            PatchResult r = await ApplyAsync(op, workspaceRoot, ct);
            results.Add(r);
            if (!r.Success)
            {
                break; // stop on first failure
            }
        }

        return results;
    }

    public async Task<bool> RollbackAsync(string backupPath, string targetFilePath, string workspaceRoot,
        CancellationToken ct = default)
    {
        // The source must be a backup this engine wrote and the target must be inside the workspace;
        // otherwise this would be an arbitrary file copy.
        PathCheck source = WorkspacePathGuard.Resolve(backupPath, workspaceRoot, BackupRoot(workspaceRoot));
        PathCheck dest = WorkspacePathGuard.Resolve(targetFilePath, workspaceRoot);
        if (!source.Ok || !dest.Ok || !File.Exists(source.FullPath))
        {
            return false;
        }

        backupPath = source.FullPath;
        string abs = dest.FullPath;
        try
        {
            File.Copy(backupPath, abs, true);
            _ = await _index.IndexFileAsync(workspaceRoot, targetFilePath, ct);
            LogRolledBack(_logger, targetFilePath, backupPath);
            return true;
        }
        catch (Exception ex)
        {
            LogRollbackFailed(_logger, ex, targetFilePath);
            return false;
        }
    }

    // ── Operation handlers ────────────────────────────────────────────────────

    private (string Content, int Lines, string? Error) ApplyReplace(string content, ReplaceOperation op)
    {
        if (string.IsNullOrEmpty(op.OldText))
        {
            return (content, 0, "OldText must not be empty.");
        }

        if (_config.RequireAnchorMatch && !content.Contains(op.OldText, StringComparison.Ordinal))
        {
            return (content, 0, $"Anchor not found in {op.FilePath}: '{Truncate(op.OldText, 60)}'");
        }

        int lines = CountLines(op.OldText);

        if (op.Occurrence == 0)
        {
            // Replace all
            string result = content.Replace(op.OldText, op.NewText, StringComparison.Ordinal);
            return (result, lines, null);
        }
        else
        {
            // Replace Nth occurrence
            int idx = FindNthOccurrence(content, op.OldText, op.Occurrence);
            if (idx < 0)
            {
                return (content, 0, $"Occurrence {op.Occurrence} of anchor not found in {op.FilePath}.");
            }

            string result = content[..idx] + op.NewText + content[(idx + op.OldText.Length)..];
            return (result, lines, null);
        }
    }

    private (string Content, int Lines, string? Error) ApplyInsert(string content, InsertOperation op)
    {
        if (string.IsNullOrEmpty(op.Anchor))
        {
            return (content, 0, "Anchor must not be empty.");
        }

        int idx = content.IndexOf(op.Anchor, StringComparison.Ordinal);
        if (_config.RequireAnchorMatch && idx < 0)
        {
            return (content, 0, $"Anchor not found in {op.FilePath}: '{Truncate(op.Anchor, 60)}'");
        }

        if (idx < 0)
        {
            idx = content.Length; // fallback: append
        }

        string result = op.Position == InsertPosition.Before
            ? content[..idx] + op.Content + content[idx..]
            : content[..(idx + op.Anchor.Length)] + op.Content + content[(idx + op.Anchor.Length)..];
        return (result, CountLines(op.Content), null);
    }

    private (string Content, int Lines, string? Error) ApplyDelete(string content, DeleteOperation op)
    {
        if (string.IsNullOrEmpty(op.Pattern))
        {
            return (content, 0, "Pattern must not be empty.");
        }

        string result;
        int lines;

        if (op.IsRegex)
        {
            try
            {
                Regex rx = new(op.Pattern, RegexOptions.Multiline);
                string before = content;
                result = rx.Replace(content, "");
                lines = CountLines(before) - CountLines(result);
            }
            catch (RegexParseException ex)
            {
                return (content, 0, $"Invalid regex: {ex.Message}");
            }
        }
        else
        {
            if (_config.RequireAnchorMatch && !content.Contains(op.Pattern, StringComparison.Ordinal))
            {
                return (content, 0, $"Pattern not found in {op.FilePath}: '{Truncate(op.Pattern, 60)}'");
            }

            lines = CountLines(op.Pattern);
            result = content.Replace(op.Pattern, "", StringComparison.Ordinal);
        }

        return (result, lines, null);
    }

    private (string Content, int Lines, string? Error) ApplyRename(string content, RenameOperation op)
    {
        if (string.IsNullOrEmpty(op.OldName) || string.IsNullOrEmpty(op.NewName))
        {
            return (content, 0, "OldName and NewName must not be empty.");
        }

        string pattern = op.WholeWordOnly
            ? $@"\b{Regex.Escape(op.OldName)}\b"
            : Regex.Escape(op.OldName);
        Regex rx = new(pattern);

        int count;
        string result;

        if (op.ReplaceInComments)
        {
            count = rx.Count(content);
            result = rx.Replace(content, op.NewName);
        }
        else
        {
            // Skip whole-line comments: any line whose first non-whitespace
            // characters start a line (//) or block (/* or leading *) comment.
            // Line endings are preserved by splitting on '\n' and re-joining.
            count = 0;
            StringBuilder sb = new(content.Length);
            string[] lines = content.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                bool isComment = trimmed.StartsWith("//", StringComparison.Ordinal)
                                || trimmed.StartsWith("/*", StringComparison.Ordinal)
                                || trimmed.StartsWith('*');

                if (isComment)
                {
                    _ = sb.Append(line);
                }
                else
                {
                    count += rx.Count(line);
                    _ = sb.Append(rx.Replace(line, op.NewName));
                }

                if (i < lines.Length - 1)
                {
                    _ = sb.Append('\n');
                }
            }

            result = sb.ToString();
        }

        if (_config.RequireAnchorMatch && count == 0)
        {
            return (content, 0, $"'{op.OldName}' not found in {op.FilePath}.");
        }

        return (result, count, null);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> WriteBackupAsync(string abs, string relPath, string workspaceRoot, CancellationToken ct)
    {
        string backupRoot = BackupRoot(workspaceRoot);
        _ = Directory.CreateDirectory(backupRoot);

        // Millisecond timestamp plus a short guid: two edits to one file inside the same second
        // must not share a backup name, or the second overwrites the first (the original).
        string ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        string unique = Guid.NewGuid().ToString("N")[..6];
        string safeName = relPath.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
        string dest = Path.Combine(backupRoot, $"{ts}_{unique}_{safeName}");

        // Byte copy: no decode/re-encode, so the backup is identical to the original.
        await using (FileStream src = new(abs, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true))
        await using (FileStream dst = new(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
        {
            await src.CopyToAsync(dst, ct);
        }

        return dest;
    }

    private static int GetPreambleLength(byte[] raw)
    {
        return raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF
            ? 3
            : raw.Length >= 2 && ((raw[0] == 0xFF && raw[1] == 0xFE) || (raw[0] == 0xFE && raw[1] == 0xFF))
                ? 2
                : 0;
    }

    /// <summary>Picks the file's own encoding (BOM-aware) so an edit does not change it. No BOM means UTF-8 without BOM.</summary>
    private static Encoding DetectEncoding(byte[] raw)
    {
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
        {
            return new UTF8Encoding(true);
        }

        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
        {
            return new UnicodeEncoding(false, true);
        }

        if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
        {
            return new UnicodeEncoding(true, true);
        }

        return new UTF8Encoding(false);
    }

    /// <summary>Writes to a temp file in the target directory, then swaps it in so a crash never leaves a half-written file.</summary>
    private static async Task WriteAtomicAsync(string abs, string content, Encoding encoding, CancellationToken ct)
    {
        string temp = $"{abs}.{Guid.NewGuid():N}.tmp";
        try
        {
            byte[] preamble = encoding.GetPreamble();
            byte[] body = encoding.GetBytes(content);
            await using (FileStream fs = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await fs.WriteAsync(preamble, ct);
                await fs.WriteAsync(body, ct);
            }

            // Antivirus/indexers can hold the target for a few ms right after the backup read.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Replace(temp, abs, null);
                    break;
                }
                catch (IOException) when (attempt < 5)
                {
                    await Task.Delay(25 * attempt, ct);
                }
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private string BackupRoot(string workspaceRoot)
    {
        return Path.IsPathRooted(_config.BackupPath)
            ? _config.BackupPath
            : Path.Combine(workspaceRoot, _config.BackupPath);
    }

    private static int FindNthOccurrence(string source, string pattern, int n)
    {
        int idx = -1;
        for (int i = 0; i < n; i++)
        {
            idx = source.IndexOf(pattern, idx + 1, StringComparison.Ordinal);
            if (idx < 0)
            {
                return -1;
            }
        }

        return idx;
    }

    private static int CountLines(string s)
    {
        return s.Count(c => c == '\n') + 1;
    }

    private static string Truncate(string s, int max)
    {
        return s.Length <= max ? s : s[..max] + "…";
    }

    private static PatchResult Fail(PatchOperation op, string error, string? backupPath = null)
    {
        return new PatchResult
        {
            Success = false,
            FilePath = op.FilePath,
            Description = op.Description,
            BackupPath = backupPath,
            Error = error
        };
    }
}
