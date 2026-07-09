namespace TokenSaver.ContextHelper.Models.Diagnostics;

/// <summary>
///     Result of <c>ContextCleanerService.RunSelfTestAsync</c> — runs a synthetic, deliberately
///     redundant/verbose payload (a block of duplicated log-style lines) through the real cleaning
///     pipeline and reports whether it actually shrank. Exercises the same code path
///     <c>BuildConciseContext</c> uses in production, so a failure here means the pipeline itself
///     is broken (a stage regressed to a no-op, or is inflating instead of trimming content) —
///     not that a particular real prompt happened not to compress well.
/// </summary>
public sealed class ContextSelfTestResult
{
    /// <summary><see langword="true" /> when <see cref="CleanedTokens" /> is strictly less than <see cref="RawTokens" />.</summary>
    public bool Passed { get; init; }

    /// <summary>Token count of the synthetic input before the pipeline ran.</summary>
    public int RawTokens { get; init; }

    /// <summary>Token count of the pipeline's output.</summary>
    public int CleanedTokens { get; init; }

    /// <summary>Percentage of <see cref="RawTokens" /> removed; negative if the output grew.</summary>
    public double SavingsPercent { get; init; }

    /// <summary>Per-stage narrative from the pipeline run, for diagnosing which stage failed to compress.</summary>
    public string Narrative { get; init; } = "";
}
