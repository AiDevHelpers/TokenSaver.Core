using TokenSaver.ContextHelper.Configuration;
using TokenSaver.ContextHelper.Models.Diagnostics;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TokenSaver.ContextHelper.Pipeline.Stages;

/// <summary>
///     Stage 3 — Code Block Collapsing.
///     Replaces fenced code blocks over the configured line threshold with a compact reference.
///     Full content is stored in <c>PipelineContext.CollapsedBlocks</c> for later retrieval.
///     Weight-aware: if CodeWeight ≥ 0.8 the threshold is raised by 30 lines.
/// </summary>
public partial class CodeBlockCollapsingStage(IOptions<ContextHelperConfig> config, ILogger<CodeBlockCollapsingStage> logger)
    : IPipelineStage
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "{Stage}: {N} blocks collapsed, saved {Saved} tokens")]
    private static partial void LogCollapseSummary(ILogger logger, string stage, int n, int saved);

    [GeneratedRegex(@"```(\w*)\n([\s\S]*?)```")]
    private static partial Regex FencedBlockRegex();

    private readonly ContextHelperConfig _config = config.Value;

    public string Name => "CodeBlockCollapsing";

    public async Task<PipelineContext> ProcessAsync(PipelineContext ctx, CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        StageDiagnostic diag = new() { StageName = Name, InputTokens = ctx.EstimateTokens() };

        // Weight adjustment: high code weight → raise threshold
        int threshold = _config.CodeBlockCollapseThreshold;
        if (ctx.Assessment.WeightProfile.CodeWeight >= 0.8)
        {
            threshold += 30;
        }

        int collapsed = 0;

        string[] processed = new string[ctx.History.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, ctx.History.Count),
            PipelineParallelism.Options,
            (i, innerCt) =>
            {
                (string? text, int count) = CollapseBlocks(ctx.History[i], ctx, threshold);
                processed[i] = text;
                _ = Interlocked.Add(ref collapsed, count);
                return ValueTask.CompletedTask;
            });

        ctx.History = [.. processed];
        (string? queryText, int queryCount) = CollapseBlocks(ctx.Query, ctx, threshold);
        ctx.Query = queryText;
        collapsed += queryCount;

        diag.OutputTokens = ctx.EstimateTokens();
        diag.ItemsCollapsed = collapsed;
        diag.Elapsed = sw.Elapsed;
        if (collapsed > 0)
        {
            diag.Notes = [$"{collapsed} code blocks collapsed (threshold={threshold} lines)"];
        }

        ctx.StageDiagnostics.Add(diag);

        LogCollapseSummary(logger, Name, collapsed, diag.TokensSaved);
        return ctx;
    }

    private static (string Text, int Count) CollapseBlocks(string text, PipelineContext ctx, int threshold)
    {
        int count = 0;
        string result = FencedBlockRegex().Replace(text, m =>
        {
            string lang = m.Groups[1].Value;
            string content = m.Groups[2].Value;
            int lines = content.Split('\n').Length;

            if (lines <= threshold)
            {
                return m.Value;
            }

            string hash = HashContent(content);
            string id = $"sha256:{hash}";

            _ = ctx.CollapsedBlocks.TryAdd(hash, m.Value);
            count++;

            return
                $"<code_block id={id} length={content.Length} lines={lines} lang={(string.IsNullOrEmpty(lang) ? "text" : lang)}>";
        });
        return (result, count);
    }

    private static string HashContent(string content)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes)[..16].ToUpperInvariant();
    }
}
