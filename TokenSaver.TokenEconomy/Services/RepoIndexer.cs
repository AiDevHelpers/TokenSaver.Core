using TokenSaver.Common.Core.Configuration;
using TokenSaver.OllamaIntegration.Services;
using TokenSaver.TokenEconomy.Contracts;
using TokenSaver.Database.Context;
using TokenSaver.Database.Context.Entities;
using TokenSaver.TokenEconomy.Models.TokenEconomy;
using TokenSaver.Utilities.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TokenSaver.TokenEconomy.Services;

/// <summary>Default <see cref="IRepoIndexer" /> — see that interface's doc comment.</summary>
public sealed partial class RepoIndexer(
    IChunkExtractor chunkExtractor,
    OllamaService ollama,
    ModelRouteResolver modelRoutes,
    TokenEconomyDbAccessor db,
    IOptions<TokenSaverConfig> config)
    : IRepoIndexer
{
    private static readonly string[] DefaultExtensions = [".cs", ".js", ".ts", ".py", ".md"];

    private static readonly string[] IgnoredSegments =
        ["node_modules", ".git", "dist", "build", "venv", ".venv", "__pycache__", "bin", "obj", ".tokensaver", ".vs"];

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

        int indexed = 0, skipped = 0, deleted = 0, failed = 0;
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
                    // A chunk whose model call keeps failing (router 500, unreachable) is skipped
                    // without writing its hash, so the next run retries only that chunk. Other
                    // chunks keep indexing. Cancellation still propagates.
                    string summary;
                    float[] vector;
                    try
                    {
                        summary = await WithRetryAsync(() => SummarizeChunkAsync(chunk, ct), ct);
                        string embedInput = _config.EnableSummaryEmbedding ? summary : chunk.Text;
                        vector = await WithRetryAsync(
                            () => ollama.EmbedAsync(modelRoutes.Resolve("embed"), embedInput, ct: ct), ct);
                    }
                    catch (HttpRequestException)
                    {
                        _ = Interlocked.Increment(ref failed);
                        return;
                    }

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
        return new RepoIndexStats(files.Count, indexed, skipped, deleted, sw.Elapsed, failed);
    }

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    /// <summary>
    ///     Retries a model call on HTTP failures (router 500, transient unreachable, or the
    ///     HttpClient request timeout) with short backoff. HttpClient timeouts surface as
    ///     <see cref="TaskCanceledException" />, so they are mapped to <see cref="HttpRequestException" />
    ///     to let the caller count and skip the chunk instead of aborting the run. Caller
    ///     cancellation is not retried. The final failure is rethrown as <see cref="HttpRequestException" />.
    /// </summary>
    private static async Task<T> WithRetryAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                if (attempt >= RetryDelays.Length)
                {
                    throw new HttpRequestException($"Model call failed after {attempt + 1} attempts: {ex.Message}", ex);
                }

                await Task.Delay(RetryDelays[attempt], ct);
            }
        }
    }

    private async Task<string> SummarizeChunkAsync(CodeChunk chunk, CancellationToken ct)
    {
        string text = chunk.Text.Length > 2000 ? chunk.Text[..2000] : chunk.Text;
        string prompt = chunk.ChunkType == "md_section"
            ? $"DOCUMENT SECTION \"{chunk.Name}\":\n{text}\n\nIn one plain sentence, what does this documentation section cover?"
            : $"CODE ({chunk.Name}):\n{text}\n\nIn one plain sentence, what does this code do? Do not rewrite it.";

        // A system prompt, because the small coder chat models treat a lone user instruction as an
        // edit request ("Here's the revised code…"). The reply is also cleaned: even with the
        // system prompt a model may add a preamble or a code block.
        string raw = await ollama.CallModelAsync(modelRoutes.Resolve("summarize"), prompt, SummarySystemPrompt, ct);
        return CleanSummary(raw);
    }

    private const string SummarySystemPrompt =
        "You write search-index descriptions of source code. Reply with exactly one plain-text sentence " +
        "(under 40 words) saying what the code does. Never rewrite, fix or repeat the code. No markdown, " +
        "no code blocks, no lists, no headings, no preamble. " +
        "Example: for a function that doubles a number, reply: Returns the input number multiplied by two.";

    private const int MaxSummaryLength = 300;

    /// <summary>
    ///     Reduces a model reply to one short description: drops fenced code blocks, preamble lines
    ///     that end in a colon, and blank lines, keeps the first remaining line, and caps its length.
    /// </summary>
    public static string CleanSummary(string raw)
    {
        string withoutFences = FencedBlock().Replace(raw, "");
        string[] lines = withoutFences.Split('\n');

        // Prefer the first line that reads like a sentence; list items, headings, "`name`: ..."
        // parameter notes and short titles are model scaffolding, not a description.
        string? sentence = lines.Select(l => l.Trim())
            .FirstOrDefault(t => t.Length > 0 && !t.EndsWith(':') && !ScaffoldLine().IsMatch(t)
                                 && t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 5);
        if (sentence is not null)
        {
            return sentence.Length <= MaxSummaryLength ? sentence : sentence[..(MaxSummaryLength - 1)].TrimEnd() + "…";
        }

        foreach (string line in lines)
        {
            string t = line.Trim().TrimStart('-', '*', '#', ' ').Trim();
            if (t.Length == 0 || t.EndsWith(':'))
            {
                continue;
            }

            return t.Length <= MaxSummaryLength ? t : t[..(MaxSummaryLength - 1)].TrimEnd() + "…";
        }

        return raw.Trim().Length <= MaxSummaryLength ? raw.Trim() : raw.Trim()[..MaxSummaryLength];
    }

    // Bullets, numbered items, headings, and "`name`:" parameter notes.
    [GeneratedRegex(@"^(?:[-*#]|\d+[.)]|`[^`]+`\s*:)")]
    private static partial Regex ScaffoldLine();

    [GeneratedRegex("```.*?```", RegexOptions.Singleline)]
    private static partial Regex FencedBlock();

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
