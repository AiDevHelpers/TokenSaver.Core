using TokenSaver.Database.Context;

using Microsoft.EntityFrameworkCore;

namespace TokenSaver.CodeHelper.Indexing;

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
        string root = string.IsNullOrEmpty(workspaceRoot) ? _config.GetWorkspaceRoot() : workspaceRoot;
        WorkspaceIndexContext ctx = await _accessor.GetDbAsync(root, ct);

        return await _accessor.WithDbLockAsync(async () => (IReadOnlyList<string>)await (
                from r in ctx.SymbolReferences
                join s1 in ctx.Symbols on r.SourceSymbolId equals s1.Id
                join s2 in ctx.Symbols on r.TargetSymbolId equals s2.Id
                where s1.File!.RelativePath == filePath && s2.File!.RelativePath != filePath
                select s2.File!.RelativePath
            ).Distinct().Take(100).ToListAsync(ct),
            ct);
    }

    /// <summary>See <see cref="GetFileDependenciesAsync" /> note — currently always empty.</summary>
    public async Task<IReadOnlyList<string>> GetFileDependentsAsync(
        string filePath, string workspaceRoot = "", CancellationToken ct = default)
    {
        string root = string.IsNullOrEmpty(workspaceRoot) ? _config.GetWorkspaceRoot() : workspaceRoot;
        WorkspaceIndexContext ctx = await _accessor.GetDbAsync(root, ct);

        return await _accessor.WithDbLockAsync(async () => (IReadOnlyList<string>)await (
                from r in ctx.SymbolReferences
                join s1 in ctx.Symbols on r.SourceSymbolId equals s1.Id
                join s2 in ctx.Symbols on r.TargetSymbolId equals s2.Id
                where s2.File!.RelativePath == filePath && s1.File!.RelativePath != filePath
                select s1.File!.RelativePath
            ).Distinct().Take(100).ToListAsync(ct),
            ct);
    }
}
