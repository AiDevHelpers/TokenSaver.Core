using TokenSaver.ContextHelper.Configuration;
using TokenSaver.ContextHelper.Models;
using TokenSaver.ContextHelper.Models.Diagnostics;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace TokenSaver.ContextHelper.Pipeline.Stages;

/// <summary>
///     Stage 8 — Chunking (runs before Stage 6 in actual pipeline order).
///     Splits history turns and query into semantic <see cref="ContextChunk" />s.
///     Each chunk is labelled with source, kind, section name, and token count.
/// </summary>
public partial class ChunkingStage : IPipelineStage
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "{Stage}: {Count} chunks produced in {Ms}ms")]
    private static partial void LogChunksProduced(ILogger logger, string stage, int count, long ms);

    private static readonly Regex MarkdownHeadingRegex =
        new(@"^#{1,3}\s+.+$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex CodeBoundaryRegex =
        new(@"(?:^(?:public|private|protected|internal|static|async)\s+(?:\w+\s+)+\w+\s*[\(\{])",
            RegexOptions.Multiline | RegexOptions.Compiled);

    private readonly ContextHelperConfig _config;
    private readonly ILogger<ChunkingStage> _logger;

    public ChunkingStage(IOptions<ContextHelperConfig> config, ILogger<ChunkingStage> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public string Name => "Chunking";

    public async Task<PipelineContext> ProcessAsync(PipelineContext ctx, CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        StageDiagnostic diag = new() { StageName = Name, InputTokens = ctx.EstimateTokens() };
        ConcurrentBag<ContextChunk> chunks = [];

        // Process history turns in parallel
        await Parallel.ForEachAsync(
            ctx.History.Select((text, idx) => (text, idx)),
            PipelineParallelism.Options,
            (item, _) =>
            {
                foreach (ContextChunk chunk in ChunkText(item.text, $"conversation:turn-{item.idx}", ContentKind.Conversation))
                {
                    chunks.Add(chunk);
                }

                return ValueTask.CompletedTask;
            });

        // Add query as its own chunk — unless this run is actually scoring an explicitly-requested
        // file's content (see PipelineContext.RequestedFileSource), in which case tag it as a real
        // File-kind chunk instead of pretending to be the live query.
        chunks.Add(ctx.RequestedFileSource != null
            ? new ContextChunk
            {
                Source = ctx.RequestedFileSource,
                SectionName = "requested file",
                Kind = ContentKind.File,
                Content = ctx.Query,
                TokenCount = PipelineContext.EstimateTokens(ctx.Query),
                PreserveVerbatim = true
            }
            : new ContextChunk
            {
                Source = "conversation:query",
                SectionName = "current query",
                Kind = ContentKind.Conversation,
                Content = ctx.Query,
                TokenCount = PipelineContext.EstimateTokens(ctx.Query),
                Relevance = 1.0
            });

        // Sort: conversation chunks in turn order, then by source
        ctx.Chunks = chunks
            .OrderBy(c => c.Source)
            .ThenBy(c => c.SectionName)
            .ToList();

        // Register all chunks in the manifest
        foreach (ContextChunk chunk in ctx.Chunks)
        {
            ctx.Manifest.Entries.Add(new ManifestEntry
            {
                ChunkId = chunk.ChunkId,
                Source = chunk.Source,
                SectionName = chunk.SectionName,
                Kind = chunk.Kind,
                TokenCount = chunk.TokenCount,
                Relevance = chunk.Relevance
            });
        }

        diag.OutputTokens = ctx.EstimateTokens();
        diag.Elapsed = sw.Elapsed;
        diag.Notes = [$"Produced {ctx.Chunks.Count} chunks"];
        ctx.StageDiagnostics.Add(diag);

        LogChunksProduced(_logger, Name, ctx.Chunks.Count, sw.ElapsedMilliseconds);
        return ctx;
    }

    private static IEnumerable<ContextChunk> ChunkText(string text, string source, ContentKind kind)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        // Detect if this is primarily code (has fenced blocks) or markdown or plain
        if (kind == ContentKind.File && MarkdownHeadingRegex.IsMatch(text))
        {
            foreach (ContextChunk c in SplitOnMarkdownHeadings(text, source, kind))
            {
                yield return c;
            }
        }
        else if (kind == ContentKind.Code || text.Contains("```", StringComparison.Ordinal))
        {
            foreach (ContextChunk c in SplitOnCodeBoundaries(text, source))
            {
                yield return c;
            }
        }
        else
        {
            foreach (ContextChunk c in SplitOnBlankLines(text, source, kind))
            {
                yield return c;
            }
        }
    }

    private static IEnumerable<ContextChunk> SplitOnMarkdownHeadings(string text, string source, ContentKind kind)
    {
        string[] parts = MarkdownHeadingRegex.Split(text);
        string[] headings = MarkdownHeadingRegex.Matches(text).Select(m => m.Value).ToArray();
        int idx = 0;

        foreach (string part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                idx++;
                continue;
            }

            string heading = idx < headings.Length ? headings[idx].TrimStart('#').Trim() : "intro";
            yield return MakeChunk(part.Trim(), source, heading, kind);
            idx++;
        }
    }

    private static IEnumerable<ContextChunk> SplitOnCodeBoundaries(string text, string source)
    {
        string[] splits = CodeBoundaryRegex.Split(text);
        string[] boundaries = CodeBoundaryRegex.Matches(text).Select(m => m.Value).ToArray();

        for (int i = 0; i < splits.Length; i++)
        {
            string content = (i < boundaries.Length ? boundaries[i] + "\n" : "") + splits[i];
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            string section = i < boundaries.Length
                ? boundaries[i].Trim()[..Math.Min(40, boundaries[i].Trim().Length)]
                : "preamble";
            yield return MakeChunk(content.Trim(), source, section, ContentKind.Code);
        }
    }

    private static IEnumerable<ContextChunk> SplitOnBlankLines(string text, string source, ContentKind kind,
        int maxTokens = 200)
    {
        StringBuilder current = new();
        int sectionIdx = 0;

        foreach (string line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line) && current.Length > 0)
            {
                string content = current.ToString().Trim();
                if (PipelineContext.EstimateTokens(content) > maxTokens)
                {
                    List<ContextChunk> subs = SplitByTokenLimit(content, source, kind, maxTokens, sectionIdx);
                    sectionIdx += subs.Count;
                    foreach (ContextChunk sub in subs)
                    {
                        yield return sub;
                    }
                }
                else
                {
                    yield return MakeChunk(content, source, $"para-{sectionIdx++}", kind);
                }

                _ = current.Clear();
            }
            else
            {
                _ = current.AppendLine(line);
            }
        }

        if (current.Length > 0)
        {
            yield return MakeChunk(current.ToString().Trim(), source, $"para-{sectionIdx}", kind);
        }
    }

    // Returns chunks produced by splitting; caller must advance its own idx counter
    private static List<ContextChunk> SplitByTokenLimit(string text, string source, ContentKind kind, int maxTokens,
        int startIdx)
    {
        List<ContextChunk> result = [];
        string[] words = text.Split(' ');
        StringBuilder buffer = new();
        int idx = startIdx;

        foreach (string word in words)
        {
            _ = buffer.Append(word);
            _ = buffer.Append(' ');
            if (PipelineContext.EstimateTokens(buffer.ToString()) >= maxTokens)
            {
                result.Add(MakeChunk(buffer.ToString().Trim(), source, $"para-{idx++}", kind));
                _ = buffer.Clear();
            }
        }

        if (buffer.Length > 0)
        {
            result.Add(MakeChunk(buffer.ToString().Trim(), source, $"para-{idx}", kind));
        }

        return result;
    }

    private static ContextChunk MakeChunk(string content, string source, string section, ContentKind kind)
    {
        return new ContextChunk
        {
            Source = source,
            SectionName = section,
            Kind = kind,
            Content = content,
            TokenCount = PipelineContext.EstimateTokens(content)
        };
    }
}
