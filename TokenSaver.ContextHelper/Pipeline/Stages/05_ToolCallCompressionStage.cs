using TokenSaver.ContextHelper.Models.Diagnostics;

using Microsoft.Extensions.Logging;

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TokenSaver.ContextHelper.Pipeline.Stages;

/// <summary>
///     Stage 5 — Tool Call Compression.
///     Distinguishes tool call types and compresses each appropriately:
///     edits → result only; reads → path + omission; searches → file list only; unknown large blobs → placeholder.
/// </summary>
public partial class ToolCallCompressionStage : IPipelineStage
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "{Stage}: {N} tool calls compressed, saved {Saved} tokens")]
    private static partial void LogCompressionSummary(ILogger logger, string stage, int n, int saved);

    // Patterns for tool call detection
    private static readonly Regex ToolTagRegex = new(@"<tool[\s\S]*?</tool>", RegexOptions.Compiled);
    private static readonly Regex InvokeTagRegex = new(@"<invoke[\s\S]*?</invoke>", RegexOptions.Compiled);

    private static readonly Regex FuncCallRegex =
        new(@"<function_calls>[\s\S]*?</function_calls>", RegexOptions.Compiled);

    private static readonly Regex LargeJsonRegex = new(@"\{[\s\S]{500,}\}", RegexOptions.Compiled);

    // Tool-type keywords
    private static readonly Regex EditToolRegex =
        new(@"(?:edit_file|write_file|create_file|Write|Edit)", RegexOptions.IgnoreCase);

    private static readonly Regex ReadToolRegex = new(@"(?:read_file|Read|cat )", RegexOptions.IgnoreCase);
    private static readonly Regex SearchToolRegex = new(@"(?:search|grep|Grep|glob|find)", RegexOptions.IgnoreCase);
    private readonly ILogger<ToolCallCompressionStage> _logger;

    public ToolCallCompressionStage(ILogger<ToolCallCompressionStage> logger)
    {
        _logger = logger;
    }

    public string Name => "ToolCallCompression";

    public Task<PipelineContext> ProcessAsync(PipelineContext ctx, CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        StageDiagnostic diag = new() { StageName = Name, InputTokens = ctx.EstimateTokens() };
        int compressed = 0;

        ctx.History = ctx.History.Select(h =>
        {
            (string? t, int n) = Compress(h);
            compressed += n;
            return t;
        }).ToList();
        (string? queryText, int queryCount) = Compress(ctx.Query);
        ctx.Query = queryText;
        compressed += queryCount;

        diag.OutputTokens = ctx.EstimateTokens();
        diag.ItemsCollapsed = compressed;
        diag.Elapsed = sw.Elapsed;
        if (compressed > 0)
        {
            diag.Notes = [$"{compressed} tool calls compressed"];
        }

        ctx.StageDiagnostics.Add(diag);

        LogCompressionSummary(_logger, Name, compressed, diag.TokensSaved);
        return Task.FromResult(ctx);
    }

    private (string Text, int Count) Compress(string text)
    {
        int count = 0;
        text = ToolTagRegex.Replace(text, m =>
        {
            (string? r, int n) = CompressMatch(m.Value);
            count += n;
            return r;
        });
        text = InvokeTagRegex.Replace(text, m =>
        {
            (string? r, int n) = CompressMatch(m.Value);
            count += n;
            return r;
        });
        text = FuncCallRegex.Replace(text, m =>
        {
            (string? r, int n) = CompressMatch(m.Value);
            count += n;
            return r;
        });
        text = LargeJsonRegex.Replace(text, m =>
        {
            count++;
            return $"<json_blob omitted length={m.Value.Length}>";
        });
        return (text, count);
    }

    private (string Result, int Count) CompressMatch(string block)
    {
        if (EditToolRegex.IsMatch(block))
        {
            string success = block.Contains("success", StringComparison.OrdinalIgnoreCase) ? "success" : "failure";
            string path = ExtractPath(block);
            return ($"<tool_call: edit_file path={path}>\n<result: {success}>", 1);
        }

        if (ReadToolRegex.IsMatch(block))
        {
            string path = ExtractPath(block);
            return ($"<tool_call: read_file path={path} omitted length={block.Length}>", 1);
        }

        if (SearchToolRegex.IsMatch(block))
        {
            IEnumerable<string> files = Regex.Matches(block, @"[\w/\\.-]+\.\w+")
                .Select(m => m.Value)
                .Distinct()
                .Take(20);
            return ($"<tool_call: search>\n<result: {string.Join(", ", files)}>", 1);
        }

        return ($"<tool_call omitted length={block.Length}>", 1);
    }

    private static string ExtractPath(string block)
    {
        Match m = Regex.Match(block, @"(?:file_path|path)["":\s]+([^\s""<>,]+)");
        return m.Success ? m.Groups[1].Value : "unknown";
    }
}
