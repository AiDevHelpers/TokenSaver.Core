#pragma warning disable CA1862 // EF Core must translate these string comparisons to SQL; StringComparison overloads do not translate.
using TokenSaver.CodeHelper.Models.Index;
using TokenSaver.Database.Context;
using TokenSaver.Database.Context.Entities;

using Microsoft.EntityFrameworkCore;

namespace TokenSaver.CodeHelper.Indexing;

/// <summary>
///     Symbol graph queries (replaces CodeIndexService): exact-name lookup, per-file symbol
///     counts, and free-text search over indexed Symbol rows.
/// </summary>
public sealed class SymbolSearchService
{
    private readonly WorkspaceIndexDbAccessor _accessor;
    private readonly WorkspaceConfigManager _config;

    public SymbolSearchService(WorkspaceIndexDbAccessor accessor, WorkspaceConfigManager config)
    {
        _accessor = accessor;
        _config = config;
    }

    public async Task<IReadOnlyList<SymbolEntry>> FindSymbolAsync(
        string name, SymbolKind? kind = null, string? filePath = null,
        string workspaceRoot = "", CancellationToken ct = default)
    {
        string requested = string.IsNullOrEmpty(workspaceRoot) ? _config.GetWorkspaceRoot() : workspaceRoot;
        (string indexRoot, string prefix) = IndexRootResolver.Resolve(requested);
        WorkspaceIndexContext ctx = await _accessor.GetDbAsync(indexRoot, ct);
        string nameUpper = name.ToUpperInvariant();

        return await _accessor.WithDbLockAsync(async () =>
        {
            IQueryable<SymbolEntity> query = ctx.Symbols.Include(s => s.File).Where(s => s.Name.ToUpperInvariant() == nameUpper);
            if (kind.HasValue)
            {
                query = query.Where(s => s.Kind == (int)kind.Value);
            }

            // Name + kind narrow in SQL; scope and file matching run in memory so they can
            // accept either index-relative or workspace-relative paths.
            List<SymbolEntity> rows = await query.ToListAsync(ct);
            IEnumerable<SymbolEntity> scoped = rows.Where(s =>
                s.File != null
                && s.File.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrEmpty(filePath) || IndexRootResolver.MatchesFilePath(s.File.RelativePath, filePath)));

            return (IReadOnlyList<SymbolEntry>)scoped.Take(50).Select(ToSymbolEntry).ToList();
        }, ct);
    }


    /// <summary>
    ///     Returns the number of indexed symbols (functions/types) in a single file, used as the
    ///     real complexity signal for <see cref="AutoWorkflow.CapabilityAssessor" /> instead of a
    ///     step-count proxy. Returns 0 if the file has not been indexed yet.
    /// </summary>
    public async Task<int> CountSymbolsInFileAsync(
        string relativeFilePath, string workspaceRoot = "", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(relativeFilePath))
        {
            return 0;
        }

        string root = string.IsNullOrEmpty(workspaceRoot) ? _config.GetWorkspaceRoot() : workspaceRoot;
        WorkspaceIndexContext ctx = await _accessor.GetDbAsync(root, ct);
        string normalized = relativeFilePath.Replace('\\', '/');

        return await _accessor.WithDbLockAsync(
            () => ctx.Symbols
                .Include(s => s.File)
                .CountAsync(s => s.File != null && s.File.RelativePath == normalized, ct),
            ct);
    }

    /// <summary>Returns the total number of indexed symbols for the workspace (0 when never scanned).</summary>
    public async Task<int> CountAllSymbolsAsync(string workspaceRoot = "", CancellationToken ct = default)
    {
        string requested = string.IsNullOrEmpty(workspaceRoot) ? _config.GetWorkspaceRoot() : workspaceRoot;
        (string indexRoot, _) = IndexRootResolver.Resolve(requested);
        WorkspaceIndexContext ctx = await _accessor.GetDbAsync(indexRoot, ct);

        return await _accessor.WithDbLockAsync(() => ctx.Symbols.CountAsync(ct), ct);
    }

    public async Task<IReadOnlyList<SymbolEntry>> SearchAsync(
        string query, int limit = 20, string workspaceRoot = "", CancellationToken ct = default)
    {
        string root = string.IsNullOrEmpty(workspaceRoot) ? _config.GetWorkspaceRoot() : workspaceRoot;
        WorkspaceIndexContext ctx = await _accessor.GetDbAsync(root, ct);
        string qUpper = query.ToUpperInvariant();

        return await _accessor.WithDbLockAsync(async () =>
        {
            List<SymbolEntity> rows = await ctx.Symbols.Include(s => s.File)
                .Where(s => s.Name.ToUpperInvariant().Contains(qUpper) || (s.FullName != null && s.FullName.ToUpperInvariant().Contains(qUpper)))
                .Take(limit)
                .ToListAsync(ct);
            return (IReadOnlyList<SymbolEntry>)rows.Select(ToSymbolEntry).ToList();
        }, ct);
    }

    private static SymbolEntry ToSymbolEntry(SymbolEntity s)
    {
        return new SymbolEntry
        {
            Id = s.Id,
            Name = s.Name,
            FullName = s.FullName ?? s.Name,
            Kind = (SymbolKind)s.Kind,
            FilePath = s.File?.RelativePath ?? "",
            Line = s.Line,
            Column = s.Col,
            ContainingType = s.ContainingType,
            Summary = s.Description,
            IndexedAt = s.UpdatedAt
        };
    }
}
