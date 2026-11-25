using AiDevTools.TokenSaver.Database.Context;

using Microsoft.EntityFrameworkCore;

namespace AiDevTools.TokenSaver.CodeHelper.Indexing;

/// <summary>
///     Cross-file symbol dependency queries, backed by SymbolReferences.
/// </summary>
public sealed class DependencyTrackingService
{
    private readonly WorkspaceIndexDbAccessor _accessor;
    private readonly WorkspaceConfigManager _config;

    public DependencyTrackingService(WorkspaceIndexDbAccessor accessor, WorkspaceConfigManager config)
    {
        _accessor = accessor;
        _config = config;
    }

    /// <summary>
    ///     NOTE: SymbolReferences is currently never populated by any ISymbolParser (same limitation
    ///     as the old code-index.db `references_` table it replaces) — this will return an empty
    ///     list until a parser starts emitting cross-file symbol edges.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetFileDependenciesAsync(
        string filePath, string workspaceRoot = "", CancellationToken ct = default)
    {
        string requested = string.IsNullOrEmpty(workspaceRoot) ? _config.GetWorkspaceRoot() : workspaceRoot;
        (string indexRoot, _) = IndexRootResolver.Resolve(requested);
        WorkspaceIndexContext ctx = await _accessor.GetDbAsync(indexRoot, ct);

        return await _accessor.WithDbLockAsync(async () =>
        {
            List<string> matched = await MatchStoredPathsAsync(ctx, filePath, ct);
            if (matched.Count == 0)
            {
                return (IReadOnlyList<string>)[];
            }

            return (IReadOnlyList<string>)await (
                from r in ctx.SymbolReferences
                join s1 in ctx.Symbols on r.SourceSymbolId equals s1.Id
                join s2 in ctx.Symbols on r.TargetSymbolId equals s2.Id
                where matched.Contains(s1.File!.RelativePath) && !matched.Contains(s2.File!.RelativePath)
                select s2.File!.RelativePath
            ).Distinct().Take(100).ToListAsync(ct);
        }, ct);
    }

    /// <summary>See <see cref="GetFileDependenciesAsync" /> note — currently always empty.</summary>
    public async Task<IReadOnlyList<string>> GetFileDependentsAsync(
        string filePath, string workspaceRoot = "", CancellationToken ct = default)
    {
        string requested = string.IsNullOrEmpty(workspaceRoot) ? _config.GetWorkspaceRoot() : workspaceRoot;
        (string indexRoot, _) = IndexRootResolver.Resolve(requested);
        WorkspaceIndexContext ctx = await _accessor.GetDbAsync(indexRoot, ct);

        return await _accessor.WithDbLockAsync(async () =>
        {
            List<string> matched = await MatchStoredPathsAsync(ctx, filePath, ct);
            if (matched.Count == 0)
            {
                return (IReadOnlyList<string>)[];
            }

            return (IReadOnlyList<string>)await (
                from r in ctx.SymbolReferences
                join s1 in ctx.Symbols on r.SourceSymbolId equals s1.Id
                join s2 in ctx.Symbols on r.TargetSymbolId equals s2.Id
                where matched.Contains(s2.File!.RelativePath) && !matched.Contains(s1.File!.RelativePath)
                select s1.File!.RelativePath
            ).Distinct().Take(100).ToListAsync(ct);
        }, ct);
    }

    /// <summary>
    ///     Maps a requested path (index-relative or workspace-relative) onto the stored Files paths
    ///     it refers to. Must run under the DB lock.
    /// </summary>
    private static async Task<List<string>> MatchStoredPathsAsync(
        WorkspaceIndexContext ctx, string filePath, CancellationToken ct)
    {
        List<string> stored = await ctx.Files.Select(f => f.RelativePath).ToListAsync(ct);
        return stored.Where(p => IndexRootResolver.MatchesFilePath(p, filePath)).ToList();
    }
}
