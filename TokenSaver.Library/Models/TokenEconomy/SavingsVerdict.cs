namespace TokenSaver.Library.Models.TokenEconomy;

/// <summary>
///     "Is this tool/role worth keeping distillation on for?" — a direct answer instead of a
///     judgment call every time, per TokenEconomyConfig.WorthItMinPct/WorthItMaxMs/WorthItMinCalls.
/// </summary>
/// <param name="WorthIt">
///     True = clears both thresholds. False = fails one of them. Null = not enough calls
///     recorded yet to judge (see
///     <see cref="TokenSaver.Library.Configuration.TokenEconomyConfig.WorthItMinCalls" />).
/// </param>
/// <param name="Reason">Human-readable explanation of the verdict, e.g. which threshold it failed.</param>
public sealed record SavingsVerdict(bool? WorthIt, string Reason);
