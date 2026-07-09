using TokenSaver.ContextHelper.Models;
using TokenSaver.ContextHelper.Models.Diagnostics;

using Microsoft.Extensions.Logging;

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace TokenSaver.ContextHelper.Pipeline.Stages;

/// <summary>
///     Stage 10 — Formatting Normalization.
///     Purely mechanical text cleanup: line endings, whitespace, indentation,
///     markdown consistency, and Unicode junk. No model calls, no async.
/// </summary>
public partial class FormattingNormalizationStage : IPipelineStage
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "{Stage}: saved {Saved} tokens in {Ms}ms")]
    private static partial void LogStageSavings(ILogger logger, string stage, int saved, long ms);

    private static readonly Regex MultipleBlankLines = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex TrailingWhitespace = new(@"[ \t]+$", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex MissingHeadingSpace =
        new(@"^(#{1,6})([^# \n])", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex UnlabelledFence = new(@"^```\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    // Zero-width and BOM characters
    private static readonly Regex ZeroWidthChars = new(@"[​‌‍﻿­]", RegexOptions.Compiled);
    private readonly ILogger<FormattingNormalizationStage> _logger;

    public FormattingNormalizationStage(ILogger<FormattingNormalizationStage> logger)
    {
        _logger = logger;
    }

    public string Name => "FormattingNormalization";

    public Task<PipelineContext> ProcessAsync(PipelineContext ctx, CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        StageDiagnostic diag = new() { StageName = Name, InputTokens = ctx.EstimateTokens() };

        foreach (ContextChunk chunk in ctx.Chunks)
        {
            chunk.Content = Normalize(chunk.Content);
        }

        ctx.Query = Normalize(ctx.Query);

        diag.OutputTokens = ctx.EstimateTokens();
        diag.Elapsed = sw.Elapsed;
        ctx.StageDiagnostics.Add(diag);

        LogStageSavings(_logger, Name, diag.TokensSaved, sw.ElapsedMilliseconds);
        return Task.FromResult(ctx);
    }

    private static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // 1. Strip BOM and zero-width characters
        text = ZeroWidthChars.Replace(text, "");

        // 2. Normalize line endings to \n
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

        // 3. Trim trailing whitespace per line
        text = TrailingWhitespace.Replace(text, "");

        // 4. Collapse 3+ blank lines to 2
        text = MultipleBlankLines.Replace(text, "\n\n");

        // 5. Ensure ATX headings have a space after #
        text = MissingHeadingSpace.Replace(text, "$1 $2");

        // 6. Label unlabelled fenced code blocks as 'text'
        text = UnlabelledFence.Replace(text, "```text");

        // 7. Normalize indentation within fenced blocks
        text = NormalizeIndentation(text);

        return text.Trim();
    }

    /// <summary>
    ///     Detects dominant indent style per fenced block and normalizes the minority.
    ///     Outside fenced blocks, indentation is left alone.
    /// </summary>
    private static string NormalizeIndentation(string text)
    {
        StringBuilder sb = new();
        string[] lines = text.Split('\n');
        bool inFence = false;
        List<string> fenceLines = [];

        foreach (string line in lines)
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (!inFence)
                {
                    inFence = true;
                    _ = sb.AppendLine(line);
                    fenceLines.Clear();
                }
                else
                {
                    // End of fence — normalize collected lines
                    IEnumerable<string> normalized = NormalizeFenceIndent(fenceLines);
                    foreach (string fl in normalized)
                    {
                        _ = sb.AppendLine(fl);
                    }

                    _ = sb.AppendLine(line);
                    inFence = false;
                }
            }
            else if (inFence)
            {
                fenceLines.Add(line);
            }
            else
            {
                _ = sb.AppendLine(line);
            }
        }

        return sb.ToString();
    }

    private static IEnumerable<string> NormalizeFenceIndent(List<string> lines)
    {
        if (lines.Count == 0)
        {
            return lines;
        }

        int tabLines = lines.Count(l => l.StartsWith('\t'));
        int spaceLines = lines.Count(l => l.StartsWith("  ", StringComparison.Ordinal));

        // If mixed, convert tabs to 4 spaces (more common in C#/JSON)
        return tabLines > 0 && spaceLines > 0 ? lines.Select(l => l.Replace("\t", "    ", StringComparison.Ordinal)) : lines;
    }
}
