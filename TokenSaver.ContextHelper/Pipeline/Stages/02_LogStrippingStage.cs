using TokenSaver.ContextHelper.Configuration;
using TokenSaver.ContextHelper.Models.Diagnostics;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TokenSaver.ContextHelper.Pipeline.Stages;

/// <summary>
///     Stage 2 — Log Stripping.
///     Collapses log blocks (&gt;3 consecutive log lines) to: omission placeholder +
///     extracted ERROR/EXCEPTION lines + last N lines verbatim.
/// </summary>
public partial class LogStrippingStage(IOptions<ContextHelperConfig> config, ILogger<LogStrippingStage> logger)
    : IPipelineStage
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "{Stage}: {Lines} log lines stripped, saved {Saved} tokens")]
    private static partial void LogStripSummary(ILogger logger, string stage, int lines, int saved);

    private static readonly Regex LogLineRegex = new(
        @"^\s*(?:\[(?:INFO|DEBUG|TRACE|WARN|WARNING|ERROR|FATAL)\]|(?:INFO|DEBUG|TRACE|WARN|WARNING|ERROR|FATAL)\s*:|\d{4}-\d{2}-\d{2}[\sT]\d{2}:\d{2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FailureRegex = new(
        @"(?:ERROR|EXCEPTION|FAIL|FATAL)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ContextHelperConfig _config = config.Value;

    public string Name => "LogStripping";

    public async Task<PipelineContext> ProcessAsync(PipelineContext ctx, CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        StageDiagnostic diag = new() { StageName = Name, InputTokens = ctx.EstimateTokens() };
        int linesStripped = 0;

        string[] processed = new string[ctx.History.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, ctx.History.Count),
            PipelineParallelism.Options,
            (i, _) =>
            {
                processed[i] = StripLogs(ctx.History[i], ref linesStripped);
                return ValueTask.CompletedTask;
            });

        ctx.History = [.. processed];
        ctx.Query = StripLogs(ctx.Query, ref linesStripped);

        diag.OutputTokens = ctx.EstimateTokens();
        diag.ItemsDropped = linesStripped;
        diag.Elapsed = sw.Elapsed;
        if (linesStripped > 0)
        {
            diag.Notes = [$"{linesStripped:N0} log lines stripped; failures + tail preserved"];
        }

        ctx.StageDiagnostics.Add(diag);

        LogStripSummary(logger, Name, linesStripped, diag.TokensSaved);
        return ctx;
    }

    private string StripLogs(string text, ref int stripped)
    {
        string[] lines = text.Split('\n');
        StringBuilder result = new();
        int i = 0;

        while (i < lines.Length)
        {
            // Detect start of a log block (3+ consecutive log lines)
            if (IsLogBlock(lines, i, out int blockEnd))
            {
                string[] block = lines[i..blockEnd];
                int totalCount = block.Length;
                string[] failures = block.Where(l => FailureRegex.IsMatch(l)).ToArray();
                string[] tail = block.TakeLast(Math.Min(_config.LogTailLines, totalCount)).ToArray();
                int omittedCount = totalCount - tail.Length;

                if (omittedCount > 0)
                {
                    _ = result.AppendLine(CultureInfo.InvariantCulture, $"<logs omitted: {omittedCount:N0} lines>");
                    _ = Interlocked.Add(ref stripped, omittedCount);
                }

                if (failures.Length > 0)
                {
                    _ = result.AppendLine("<failures>");
                    foreach (string? f in failures)
                    {
                        _ = result.AppendLine("  " + f.TrimStart());
                    }

                    _ = result.AppendLine("</failures>");
                }

                if (tail.Length > 0)
                {
                    _ = result.AppendLine(CultureInfo.InvariantCulture, $"<log tail: last {tail.Length} lines>");
                    foreach (string? t in tail)
                    {
                        _ = result.AppendLine(t);
                    }

                    _ = result.AppendLine("</log tail>");
                }

                i = blockEnd;
            }
            else
            {
                _ = result.AppendLine(lines[i]);
                i++;
            }
        }

        return result.ToString().TrimEnd();
    }

    private static bool IsLogBlock(string[] lines, int start, out int end)
    {
        end = start;
        int count = 0;
        for (int j = start; j < lines.Length; j++)
        {
            if (LogLineRegex.IsMatch(lines[j]))
            {
                count++;
                end = j + 1;
            }
            else if (count >= 3)
            {
                break;
            }
            else
            {
                end = start;
                return false;
            }
        }

        return count >= 3;
    }
}
