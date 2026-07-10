namespace TokenSaver.Library.Models.TokenEconomy;

/// <summary>
///     One aggregated row from the TokenSavings table, grouped by tool or role — the shape
///     returned by both TokenSavingsReport (many rows, ranked) and WorthKeeping (one row).
/// </summary>
public sealed record SavingsAggregate(
    string Name,
    int Calls,
    long RawTokens,
    long DistilledTokens,
    long SavedTokens,
    double? PctSaved,
    double? AvgDurationMs,
    double? AvgConversationTurns = null,
    double? AvgConversationTokens = null)
{
    public static readonly SavingsAggregate Empty = new("", 0, 0, 0, 0, null, null);
}
