namespace TokenSaver.Library.Models.TokenEconomy;

/// <summary>Result of a DistillationService.DistillAsync call.</summary>
/// <param name="Summary">The compressed (or, if distillation is disabled/input is empty, raw-capped) text.</param>
/// <param name="RawTokens">Estimated token count of the original input.</param>
/// <param name="DistilledTokens">Estimated token count of <see cref="Summary" />.</param>
/// <param name="Terse">Whether terse-mode prompts/limits were actually used for this call.</param>
/// <param name="Distilled">False when the master EnableDistill switch was off (or input was empty) and Summary is raw/untouched text rather than an Ollama-compressed result.</param>
/// <param name="RawArtifactPath">Path the caller wrote raw output to before distilling, if raw retention is enabled and the caller supplied one — surfaced back so the tool response can offer it as an escape hatch.</param>
public sealed record DistillResult(
    string Summary,
    int RawTokens,
    int DistilledTokens,
    bool Terse,
    bool Distilled,
    string? RawArtifactPath = null)
{
    public int SavedTokens => RawTokens - DistilledTokens;
}
