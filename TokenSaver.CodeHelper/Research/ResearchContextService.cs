using TokenSaver.OllamaIntegration.Services;
using TokenSaver.Library.Services;

using Microsoft.Extensions.Logging;

using System.Globalization;
using System.Text;

namespace TokenSaver.CodeHelper.Research;

/// <summary>
///     Scans the workspace for files relevant to a research or planning prompt,
///     scores them via the draft model, and returns an enriched context block
///     ready to prepend to the prompt before handing off to the calling AI.
///     Behaviour is shaped by ResearchContextFormat.WillAskForMissingContext:
///     true  — send summaries + retrieval hints (client will re-read files itself)
///     false — embed full content for top files, summaries for mid-tier (Copilot, Continue)
///     The caller (MCP tool layer) resolves the format via AgentHelpers and passes it in,
///     keeping this service free of TokenSaver.AgentHelpers.Core.
/// </summary>
public sealed partial class ResearchContextService
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Research context: scoring {N} candidate files")]
    private static partial void LogScoringCandidates(ILogger logger, int n);

    [LoggerMessage(Level = LogLevel.Information, Message = "Research context: {H} high-relevance, {M} mid-relevance files")]
    private static partial void LogRelevanceCounts(ILogger logger, int h, int m);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Score failed for {File}")]
    private static partial void LogScoreFailed(ILogger logger, Exception ex, string file);

    [LoggerMessage(Level = LogLevel.Warning, Message = "File collection partially failed")]
    private static partial void LogFileCollectionFailed(ILogger logger, Exception ex);

    private const int MaxFilesToScore = 40;
    private const int MaxParallelScores = 6;
    private const double HighThreshold = 0.65;
    private const double MidThreshold = 0.35;
    private const int MaxFullEmbedChars = 8_000;

    private static readonly string[] TextExtensions =
    [
        ".md", ".txt", ".cs", ".ts", ".js", ".py", ".go", ".java", ".json",
        ".yaml", ".yml", ".xml", ".toml", ".ini", ".config", ".rst", ".html"
    ];

    private static readonly string[] ExcludeDirs =
    [
        "bin", "obj", "node_modules", ".git", "dist", "out", ".local-mcp",
        ".tokensaver", "packages", ".vs", "__pycache__"
    ];

    private readonly ILogger<ResearchContextService> _logger;

    private readonly OllamaService _ollama;

    public ResearchContextService(
        OllamaService ollama,
        ILogger<ResearchContextService> logger)
    {
        _ollama = ollama;
        _logger = logger;
    }

    /// <summary>
    ///     Builds an enriched research context block for the given prompt.
    ///     Returns empty string when workspace root is unknown or no files score above threshold.
    /// </summary>
    public async Task<string> BuildResearchContextAsync(
        string prompt,
        string workspaceRoot,
        ResearchContextFormat? format = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
        {
            return "";
        }

        ResearchContextFormat fmt = format ?? ResearchContextFormat.Default;

        List<string> candidates = CollectCandidates(workspaceRoot);
        if (candidates.Count == 0)
        {
            return "";
        }

        LogScoringCandidates(_logger, candidates.Count);

        List<ScoredFile> scored = await ScoreFilesAsync(candidates, prompt, workspaceRoot, ct);

        List<ScoredFile> high = scored.Where(s => s.Score >= HighThreshold).OrderByDescending(s => s.Score).ToList();
        List<ScoredFile> mid = scored.Where(s => s.Score is >= MidThreshold and < HighThreshold).OrderByDescending(s => s.Score)
            .ToList();

        if (high.Count == 0 && mid.Count == 0)
        {
            return "";
        }

        LogRelevanceCounts(_logger, high.Count, mid.Count);

        return fmt.WillAskForMissingContext
            ? BuildHintBlock(high, mid, fmt)
            : await BuildEmbedBlockAsync(high, mid, fmt);
    }

    // ── File collection ───────────────────────────────────────────────────────

    private List<string> CollectCandidates(string root)
    {
        List<string> results = [];
        try
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, file);
                if (IsExcluded(rel))
                {
                    continue;
                }

                if (!TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add(file);
                if (results.Count >= MaxFilesToScore)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            LogFileCollectionFailed(_logger, ex);
        }

        return results;
    }

    private static bool IsExcluded(string rel)
    {
        string[] parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => ExcludeDirs.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    // ── Scoring ───────────────────────────────────────────────────────────────

    private async Task<List<ScoredFile>> ScoreFilesAsync(
        List<string> files, string prompt, string root, CancellationToken ct)
    {
        List<ScoredFile> results = [];
        using SemaphoreSlim sem = new(MaxParallelScores);

        await Task.WhenAll(files.Select(async file =>
        {
            await sem.WaitAsync(ct);
            try
            {
                ScoredFile s = await ScoreFileAsync(file, prompt, root, ct);
                lock (results)
                {
                    results.Add(s);
                }
            }
            catch (Exception ex)
            {
                LogScoreFailed(_logger, ex, file);
            }
            finally
            {
                _ = sem.Release();

            }
        }));

        return results;
    }

    private async Task<ScoredFile> ScoreFileAsync(
        string absPath, string prompt, string root, CancellationToken ct)
    {
        string rel = Path.GetRelativePath(root, absPath);
        string content;
        try
        {
            content = await File.ReadAllTextAsync(absPath, ct);
        }
        catch
        {
            return new ScoredFile(absPath, rel, 0.0, "", "");
        }

        if (content.Length == 0)
        {
            return new ScoredFile(absPath, rel, 0.0, "", "");
        }

        string preview = content.Length > 3_000 ? content[..3_000] : content;
        string scorePrompt =
            $"Rate relevance of this file to the task (0.0–1.0) and give a one-sentence summary.\n" +
            $"Task: {prompt}\n" +
            $"File: {rel}\n" +
            $"Content preview:\n{preview}\n\n" +
            $"Reply with exactly two lines:\n" +
            $"SCORE: <number>\n" +
            $"SUMMARY: <one sentence>";

        string response = await _ollama.CallModelAsync("HelperModel:draft", scorePrompt, ct);
        (double score, string? summary) = ParseScoreResponse(response);
        return new ScoredFile(absPath, rel, score, summary, content);
    }

    private static (double Score, string Summary) ParseScoreResponse(string response)
    {
        double score = 0.0;
        string summary = "";
        foreach (string line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("SCORE:", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(line["SCORE:".Length..].Trim(),
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture, out double d))
                {
                    score = Math.Clamp(d, 0.0, 1.0);
                }
            }
            else if (line.StartsWith("SUMMARY:", StringComparison.OrdinalIgnoreCase))
            {
                summary = line["SUMMARY:".Length..].Trim();
            }
        }

        return (score, summary);
    }

    // ── Context block: hint mode (WillAskForMissingContext = true) ────────────

    private static string BuildHintBlock(
        List<ScoredFile> high, List<ScoredFile> mid, ResearchContextFormat fmt)
    {
        StringBuilder sb = new();
        AppendHeader(sb, fmt, "Research Context");

        if (high.Count > 0)
        {
            AppendSection(sb, fmt, "Highly Relevant Files");
            foreach (ScoredFile f in high)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- **{f.RelPath}** (relevance {f.Score:P0})");
                if (!string.IsNullOrEmpty(f.Summary))
                {
                    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  {f.Summary}");
                }

                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  _To read: request the full content of `{f.RelPath}`_");
            }

            _ = sb.AppendLine();
        }

        if (mid.Count > 0)
        {
            AppendSection(sb, fmt, "Potentially Relevant Files");
            foreach (ScoredFile f in mid)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- **{f.RelPath}** (relevance {f.Score:P0})");
                if (!string.IsNullOrEmpty(f.Summary))
                {
                    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  {f.Summary}");
                }
            }

            _ = sb.AppendLine();
        }

        AppendFooter(sb, fmt);
        return sb.ToString().TrimEnd();
    }

    // ── Context block: embed mode (WillAskForMissingContext = false) ──────────

    private static async Task<string> BuildEmbedBlockAsync(
        List<ScoredFile> high, List<ScoredFile> mid,
        ResearchContextFormat fmt)
    {
        StringBuilder sb = new();
        int charsUsed = 0;
        int perFileLimit = fmt.PerFileCharLimit > 0 ? fmt.PerFileCharLimit : MaxFullEmbedChars;
        List<ScoredFile> deferred = [];

        AppendHeader(sb, fmt, "Research Context");
        AppendSection(sb, fmt, "Highly Relevant Files");

        foreach (ScoredFile f in high)
        {
            if (charsUsed >= fmt.CharBudget)
            {
                deferred.Add(f);
                continue;
            }

            string body = f.Content.Length > perFileLimit ? f.Content[..perFileLimit] : f.Content;
            bool truncated = f.Content.Length > perFileLimit;

            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"### {f.RelPath}  (relevance {f.Score:P0})");
            if (!string.IsNullOrEmpty(f.Summary))
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"_{f.Summary}_");
            }

            _ = sb.AppendLine();
            _ = sb.AppendLine("```");
            _ = sb.AppendLine(body);
            if (truncated)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"[... {f.Content.Length - perFileLimit} chars omitted ...]");
            }

            _ = sb.AppendLine("```");
            _ = sb.AppendLine();
            charsUsed += body.Length;
        }

        if (deferred.Count > 0 || mid.Count > 0)
        {
            AppendSection(sb, fmt, "Additional Relevant Files (summaries)");
            foreach (ScoredFile? f in deferred.Concat(mid))
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- **{f.RelPath}** (relevance {f.Score:P0}): {f.Summary}");
            }

            _ = sb.AppendLine();
        }

        AppendFooter(sb, fmt);
        return sb.ToString().TrimEnd();
    }

    // ── Formatting helpers ────────────────────────────────────────────────────

    private static void AppendHeader(StringBuilder sb, ResearchContextFormat fmt, string title)
    {
        if (fmt.UseXmlSectionTags)
        {
            _ = sb.AppendLine("<research_context>");
        }
        else if (fmt.UseMarkdownHeaders)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"## {title}");
        }

        _ = sb.AppendLine();
    }

    private static void AppendSection(StringBuilder sb, ResearchContextFormat fmt, string title)
    {
        if (fmt.UseMarkdownHeaders)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"### {title}");
        }

        _ = sb.AppendLine();
    }

    private static void AppendFooter(StringBuilder sb, ResearchContextFormat fmt)
    {
        if (fmt.UseXmlSectionTags)
        {
            _ = sb.AppendLine("</research_context>");
        }
    }

    // ── Model ─────────────────────────────────────────────────────────────────

    private sealed record ScoredFile(
        string AbsPath,
        string RelPath,
        double Score,
        string Summary,
        string Content);
}
