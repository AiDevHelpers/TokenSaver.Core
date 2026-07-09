using TokenSaver.ContextHelper.Models;
using TokenSaver.ContextHelper.Models.Diagnostics;

using Microsoft.Extensions.Logging;

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TokenSaver.ContextHelper.Pipeline.Stages;

/// <summary>
///     Stage 9 — Conversational Garbage Removal.
///     Strips filler phrases, AI self-references, apologies, and disclaimers at sentence granularity.
///     Informational content in the same sentence is preserved.
/// </summary>
public partial class ConversationalGarbageStage : IPipelineStage
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "{Stage}: saved {Saved} tokens")]
    private static partial void LogStageSavings(ILogger logger, string stage, int saved);

    // Each pattern targets a garbage phrase; the replacement strips the phrase but preserves
    // any non-garbage remainder in the same sentence.
    private static readonly (Regex Pattern, string Replacement)[] GarbageRules =
    [
        (new Regex(@"\bSure,?\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase), ""),
        (new Regex(@"\bOf course[,!]?\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase), ""),
        (new Regex(@"\bCertainly[,!]?\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase), ""),
        (new Regex(@"\bAbsolutely[,!]?\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase), ""),
        (new Regex(@"\bGreat[,!]\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase), ""),
        (new Regex(@"Let me know if you need\s+\w+.*?[.!?]", RegexOptions.Compiled | RegexOptions.IgnoreCase), ""),
        (new Regex(@"Feel free to\s+\w+.*?[.!?]", RegexOptions.Compiled | RegexOptions.IgnoreCase), ""),
        (new Regex(@"Hope (?:this|that) helps.*?[.!?]", RegexOptions.Compiled | RegexOptions.IgnoreCase), ""),
        (new Regex(@"\bhappy to help\b.*?[.!?]", RegexOptions.Compiled | RegexOptions.IgnoreCase), ""),
        (new Regex(@"As an AI\b.*?[.!?]", RegexOptions.Compiled | RegexOptions.IgnoreCase), ""),
        (new Regex(@"As a language model\b.*?[.!?]", RegexOptions.Compiled | RegexOptions.IgnoreCase), ""),
        (new Regex(@"I don'?t have feelings\b.*?[.!?]", RegexOptions.Compiled | RegexOptions.IgnoreCase), ""),
        (new Regex(@"I apologize\b.*?[.!?]", RegexOptions.Compiled | RegexOptions.IgnoreCase), ""),
        (new Regex(@"I'?m sorry\b.*?[.!?]", RegexOptions.Compiled | RegexOptions.IgnoreCase), ""),
        (new Regex(@"Sorry for\b.*?[.!?]", RegexOptions.Compiled | RegexOptions.IgnoreCase), ""),
        (new Regex(@"Please note that\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase), ""),
        (new Regex(@"It'?s worth (?:mentioning|noting)\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase), ""),
        (new Regex(@"Keep in mind that\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase), "")
    ];

    private readonly ILogger<ConversationalGarbageStage> _logger;

    public ConversationalGarbageStage(ILogger<ConversationalGarbageStage> logger)
    {
        _logger = logger;
    }

    public string Name => "ConversationalGarbage";

    public Task<PipelineContext> ProcessAsync(PipelineContext ctx, CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        StageDiagnostic diag = new() { StageName = Name, InputTokens = ctx.EstimateTokens() };

        foreach (ContextChunk chunk in ctx.Chunks)
        {
            chunk.Content = CleanGarbage(chunk.Content);
        }

        ctx.Query = CleanGarbage(ctx.Query);

        diag.OutputTokens = ctx.EstimateTokens();
        diag.Elapsed = sw.Elapsed;
        ctx.StageDiagnostics.Add(diag);

        LogStageSavings(_logger, Name, diag.TokensSaved);
        return Task.FromResult(ctx);
    }

    private static string CleanGarbage(string text)
    {
        foreach ((Regex? pattern, string? replacement) in GarbageRules)
        {
            text = pattern.Replace(text, replacement);
        }

        // Collapse runs of whitespace introduced by removals
        text = Regex.Replace(text, @"[ \t]{2,}", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }
}
