using TokenSaver.Library.Models.TokenEconomy;

namespace TokenSaver.Library.Contracts;

/// <summary>
///     Records per-distill()-call token savings and answers "is this worth keeping on" queries
///     against the recorded history. Implementations own the ENABLE_SAVINGS_TRACKING kill-switch
///     check internally — <see cref="RecordAsync" /> is always safe for callers to invoke
///     unconditionally (distill() does so on every call); when tracking is disabled it's a no-op.
/// </summary>
public interface ISavingsTracker
{
    /// <summary>
    ///     Records one distill() call's outcome. No-op when
    ///     <see cref="Configuration.TokenEconomyConfig.EnableSavingsTracking" /> is false.
    /// </summary>
    Task RecordAsync(
        string workspaceRoot,
        string tool,
        string role,
        bool terse,
        int rawTokens,
        int distilledTokens,
        int durationMs,
        CancellationToken ct = default);

    /// <summary>
    ///     Aggregated rows grouped by tool or role over the last <paramref name="sinceDays" /> days,
    ///     ranked by saved tokens descending (the top row answers "what's saving the most").
    /// </summary>
    Task<IReadOnlyList<SavingsAggregate>> GetReportAsync(
        string workspaceRoot,
        int sinceDays,
        bool groupByRole,
        CancellationToken ct = default);

    /// <summary>Aggregate for exactly one tool or role name, or <see cref="SavingsAggregate.Empty" /> if there's no data yet.</summary>
    Task<SavingsAggregate> GetOneAsync(
        string workspaceRoot,
        string target,
        bool groupByRole,
        int sinceDays,
        CancellationToken ct = default);

    /// <summary>Computes the worth-it verdict for an aggregate, per TokenEconomyConfig's thresholds.</summary>
    SavingsVerdict Verdict(SavingsAggregate aggregate);
}
