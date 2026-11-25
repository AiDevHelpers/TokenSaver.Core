using AiDevTools.TokenSaver.TokenEconomy.Models.TokenEconomy;

namespace AiDevTools.TokenSaver.TokenEconomy.Contracts;

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
    ///     <paramref name="conversationTurns" />/<paramref name="conversationTokens" /> capture the
    ///     whole conversation's size at the time of this call (0 for non-conversational tools like
    ///     RunTests/ContainerLogs that have no history/turns concept).
    /// </summary>
    Task RecordAsync(
        string workspaceRoot,
        string tool,
        string role,
        bool terse,
        int rawTokens,
        int distilledTokens,
        int durationMs,
        int conversationTurns = 0,
        int conversationTokens = 0,
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
