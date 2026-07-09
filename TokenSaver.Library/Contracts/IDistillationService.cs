using TokenSaver.Library.Models.TokenEconomy;

namespace TokenSaver.Library.Contracts;

/// <summary>
///     Compresses bulk tool output (test runner output, logs, diffs, lint results, ...) into a
///     short, role-specific summary via the local model before it's returned to the calling AI —
///     the "no tool returns raw bulk text to Claude" design. See
///     TokenSaver/docs/token-economy-tools-contract.md for the full behavioral spec.
/// </summary>
public interface IDistillationService
{
    /// <summary>
    ///     Compresses <paramref name="raw" /> using the prompt for <paramref name="role" />.
    ///     Returns raw text (capped, undistilled) unchanged when EnableDistill is off or
    ///     <paramref name="raw" /> is empty/whitespace — callers should not need to special-case
    ///     this, just use <see cref="DistillResult.Distilled" /> if they care.
    /// </summary>
    /// <param name="workspaceRoot">Used to key the shared savings-tracking DB — pass TokenSaverConfig.WorkspaceRoot.</param>
    /// <param name="tool">Calling tool name, recorded for TokenSavingsReport/WorthKeeping, e.g. "runTests".</param>
    /// <param name="role">Which role-specific prompt to use.</param>
    /// <param name="raw">The bulk text to compress.</param>
    /// <param name="maxTokens">Requested output token ceiling — halved (by TokenEconomyConfig.TerseMaxTokensRatio) when terse mode is active.</param>
    /// <param name="terse">Per-call override of the server's TokenEconomyConfig.Terse default. Null = use the server default.</param>
    /// <param name="model">Ollama model to call. Defaults to the "summarize" entry in TokenEconomyConfig.ModelRoutes.</param>
    Task<DistillResult> DistillAsync(
        string workspaceRoot,
        string tool,
        DistillRole role,
        string raw,
        int maxTokens = 300,
        bool? terse = null,
        string? model = null,
        CancellationToken ct = default);

    /// <summary>Rough token-count estimate (chars/4) — same heuristic used for RawTokens/DistilledTokens throughout. Exposed for TokenBudget.</summary>
    int EstimateTokens(string text);
}
