using TokenSaver.Common.Core.Configuration;
using TokenSaver.TokenEconomy.Contracts;
using TokenSaver.Database.Context;
using TokenSaver.Database.Context.Entities;
using TokenSaver.TokenEconomy.Models.TokenEconomy;

using Microsoft.Extensions.Options;

namespace TokenSaver.TokenEconomy.Services;

/// <summary>
///     EF Core/SQLite-backed <see cref="ISavingsTracker" />, storing rows in the shared
///     ".tokensaver/index.db" via <see cref="TokenEconomyDbAccessor" /> — see that type's
///     doc comment for why this doesn't reuse TokenSaver.CodeHelper's WorkspaceIndexDbAccessor.
/// </summary>
public sealed class SavingsTracker(TokenEconomyDbAccessor db, IOptions<TokenSaverConfig> config) : ISavingsTracker
{
    private readonly TokenEconomyConfig _config = config.Value.TokenEconomy;

    public async Task RecordAsync(
        string workspaceRoot,
        string tool,
        string role,
        bool terse,
        int rawTokens,
        int distilledTokens,
        int durationMs,
        int conversationTurns = 0,
        int conversationTokens = 0,
        CancellationToken ct = default)
    {
        if (!_config.EnableSavingsTracking)
        {
            return;
        }

        WorkspaceIndexContext ctx = await db.GetDbAsync(workspaceRoot, ct);
        await db.WithDbLockAsync(async () =>
        {
            _ = ctx.TokenSavings.Add(new TokenSavingsEntity
            {
                Tool = tool,
                Role = role,
                Terse = terse,
                RawTokens = rawTokens,
                DistilledTokens = distilledTokens,
                SavedTokens = rawTokens - distilledTokens,
                DurationMs = durationMs,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ConversationTurns = conversationTurns,
                ConversationTokens = conversationTokens
            });
            _ = await ctx.SaveChangesAsync(ct);
        }, ct);
    }

    public async Task<IReadOnlyList<SavingsAggregate>> GetReportAsync(
        string workspaceRoot, int sinceDays, bool groupByRole, CancellationToken ct = default)
    {
        WorkspaceIndexContext ctx = await db.GetDbAsync(workspaceRoot, ct);
        long since = DateTimeOffset.UtcNow.AddDays(-sinceDays).ToUnixTimeMilliseconds();

        return await db.WithDbLockAsync(async () =>
        {
            List<TokenSavingsEntity> rows = ctx.TokenSavings
                .Where(t => t.CreatedAt >= since)
                .ToList();

            IEnumerable<IGrouping<string, TokenSavingsEntity>> groups = groupByRole
                ? rows.GroupBy(r => r.Role)
                : rows.GroupBy(r => r.Tool);

            List<SavingsAggregate> results = groups
                .Select(Aggregate)
                .OrderByDescending(a => a.SavedTokens)
                .ToList();

            return (IReadOnlyList<SavingsAggregate>)results;
        }, ct);
    }

    public async Task<SavingsAggregate> GetOneAsync(
        string workspaceRoot, string target, bool groupByRole, int sinceDays, CancellationToken ct = default)
    {
        WorkspaceIndexContext ctx = await db.GetDbAsync(workspaceRoot, ct);
        long since = DateTimeOffset.UtcNow.AddDays(-sinceDays).ToUnixTimeMilliseconds();

        return await db.WithDbLockAsync(async () =>
        {
            List<TokenSavingsEntity> rows = ctx.TokenSavings
                .Where(t => t.CreatedAt >= since && (groupByRole ? t.Role == target : t.Tool == target))
                .ToList();

            return rows.Count == 0 ? SavingsAggregate.Empty with { Name = target } : Aggregate(rows.GroupBy(_ => target).First());
        }, ct);
    }

    public SavingsVerdict Verdict(SavingsAggregate aggregate)
    {
        if (aggregate.Calls < _config.WorthItMinCalls)
        {
            return new SavingsVerdict(null, $"not enough calls yet ({aggregate.Calls} < {_config.WorthItMinCalls}) to judge");
        }

        double pct = aggregate.PctSaved ?? 0;
        if (pct < _config.WorthItMinPct)
        {
            return new SavingsVerdict(false, $"only {pct:F1}% saved, below {_config.WorthItMinPct}% threshold");
        }

        double ms = aggregate.AvgDurationMs ?? 0;
        if (ms > _config.WorthItMaxMs)
        {
            return new SavingsVerdict(false, $"avg {ms:F0}ms per call exceeds {_config.WorthItMaxMs}ms threshold");
        }

        return new SavingsVerdict(true, $"{pct:F1}% saved at {ms:F0}ms avg — clears both thresholds");
    }

    private static SavingsAggregate Aggregate(IGrouping<string, TokenSavingsEntity> group)
    {
        List<TokenSavingsEntity> list = group.ToList();
        long rawTokens = list.Sum(r => (long)r.RawTokens);
        long distilledTokens = list.Sum(r => (long)r.DistilledTokens);
        long savedTokens = list.Sum(r => (long)r.SavedTokens);

        return new SavingsAggregate(
            group.Key,
            list.Count,
            rawTokens,
            distilledTokens,
            savedTokens,
            rawTokens > 0 ? Math.Round(100.0 * savedTokens / rawTokens, 1) : null,
            list.Count > 0 ? Math.Round(list.Average(r => (double)r.DurationMs), 0) : null,
            list.Count > 0 ? Math.Round(list.Average(r => (double)r.ConversationTurns), 1) : null,
            list.Count > 0 ? Math.Round(list.Average(r => (double)r.ConversationTokens), 0) : null);
    }
}
