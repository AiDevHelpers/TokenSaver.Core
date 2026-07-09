using TokenSaver.CodeHelper.Models.Planning;

using System.Collections.Concurrent;

namespace TokenSaver.CodeHelper.Planning;

/// <summary>
///     In-memory, session-scoped store for active code plans.
///     No persistence — plans live for the lifetime of the server process.
/// </summary>
public sealed class PlanStore
{
    private readonly ConcurrentDictionary<string, CodePlan> _plans = new();

    public void Save(CodePlan plan)
    {
        _plans[plan.PlanId] = plan;
    }

    public CodePlan? Get(string planId)
    {
        return _plans.TryGetValue(planId, out CodePlan? p) ? p : null;
    }

    public IReadOnlyList<CodePlan> GetAll(string? sessionId = null)
    {
        return _plans.Values
            .Where(p => sessionId == null || p.SessionId == sessionId)
            .OrderByDescending(p => p.CreatedAt)
            .ToList();
    }

    public bool TryUpdate(string planId, CodePlan updated)
    {
        if (!_plans.ContainsKey(planId))
        {
            return false;
        }

        _plans[planId] = updated;
        return true;
    }
}
