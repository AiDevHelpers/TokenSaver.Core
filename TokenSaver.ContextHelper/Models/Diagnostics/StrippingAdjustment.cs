namespace TokenSaver.ContextHelper.Models.Diagnostics;

/// <summary>
///     Session-scoped overrides to stripping thresholds produced by <c>StrippingHealthAnalyzer</c>.
///     Never mutates <c>ContextHelperConfig</c> — applied only for the current build.
/// </summary>
public record StrippingAdjustment(
    int? CodeBlockCollapseThreshold = null,
    double? SemanticThreshold = null,
    int? VerbatimTurns = null
);
