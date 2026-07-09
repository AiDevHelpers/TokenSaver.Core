using TokenSaver.ContextHelper.Models;
using TokenSaver.ContextHelper.Models.Diagnostics;

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;

namespace TokenSaver.ContextHelper.Pipeline;

public class PipelineContext
{
    // ── Input ──────────────────────────────────────────────────────────────────

    public string Query { get; set; } = "";
    public IReadOnlyList<string> History { get; set; } = [];
    public int TokenBudget { get; set; }
    public string? SessionId { get; set; }

    /// <summary>
    ///     When true, the consuming model performs internal chain-of-thought reasoning
    ///     (e.g. DeepSeek R1).  Stage 04 will use a direct summarisation prompt that
    ///     avoids triggering additional deliberation, and Stage 09 strips hedging phrases
    ///     more aggressively to avoid polluting the model's reasoning context.
    /// </summary>
    public bool AvoidChainOfThought { get; set; }

    // ── Assessment (set before Stage 1 by PromptAssessor) ─────────────────────

    public PromptAssessment Assessment { get; set; } = new();

    // ── Per-request mutable state (never on singleton stages) ─────────────────

    /// <summary>SHA-256 prefixes of code blocks already seen — used by Stage 1.</summary>
    public ConcurrentDictionary<string, byte> SeenBlockHashes { get; } = new();

    /// <summary>Full content of collapsed code blocks keyed by sha256 prefix — Stage 3.</summary>
    public ConcurrentDictionary<string, string> CollapsedBlocks { get; } = new();

    /// <summary>Working list of chunks produced by Stage 8 and filtered by Stage 6.</summary>
    public ICollection<ContextChunk> Chunks { get; set; } = [];

    // ── Diagnostics ───────────────────────────────────────────────────────────

    public Collection<StageDiagnostic> StageDiagnostics { get; } = [];
    public ContextManifest Manifest { get; } = new();
    public SpaghettiReport? SpaghettiReport { get; set; }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public int EstimateTokens()
    {
        StringBuilder sb = new();
        _ = sb.Append(Query);
        foreach (string h in History)
        {
            _ = sb.Append(h);
        }

        foreach (ContextChunk c in Chunks)
        {
            _ = sb.Append(c.Content);
        }

        string[] words = sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return Math.Max(1, (int)Math.Ceiling(words.Length * 1.3));
    }

    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return Math.Max(1, (int)Math.Ceiling(words.Length * 1.3));
    }
}
