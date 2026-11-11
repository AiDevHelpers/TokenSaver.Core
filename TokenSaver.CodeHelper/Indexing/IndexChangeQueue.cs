using System.Collections.Concurrent;

using TokenSaver.CodeHelper.Models.Index;

namespace TokenSaver.CodeHelper.Indexing;

/// <summary>
///     Pending workspace-relative paths per root, flushed through
///     <see cref="WorkspaceIndexService.IndexPathsAsync" />. Codegen and agent edits push paths here;
///     a repeated path collapses into one entry. Whole-root passes (git watcher, triggers) run on their
///     own schedule and still go through the same per-root gate.
/// </summary>
public sealed class IndexChangeQueue
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    public void Enqueue(string root, IEnumerable<string> relPaths)
    {
        ConcurrentDictionary<string, byte> set =
            _pending.GetOrAdd(Path.GetFullPath(root), _ => new(StringComparer.OrdinalIgnoreCase));
        foreach (string p in relPaths)
        {
            string normalized = p.Replace('\\', '/').TrimStart('/');
            if (normalized.Length > 0)
            {
                set[normalized] = 0;
            }
        }
    }

    /// <summary>Number of paths waiting for <paramref name="root" />.</summary>
    public int PendingCount(string root) =>
        _pending.TryGetValue(Path.GetFullPath(root), out ConcurrentDictionary<string, byte>? set) ? set.Count : 0;

    /// <summary>
    ///     Drains everything pending for <paramref name="root" /> through the index. Returns null when
    ///     nothing was pending. If the index pass throws, the batch is re-queued and the exception rethrown.
    /// </summary>
    public async Task<IndexStats?> FlushAsync(WorkspaceIndexService index, string root, CancellationToken ct = default)
    {
        if (!_pending.TryGetValue(Path.GetFullPath(root), out ConcurrentDictionary<string, byte>? set) || set.IsEmpty)
        {
            return null;
        }

        List<string> batch = set.Keys.ToList();
        foreach (string p in batch)
        {
            _ = set.TryRemove(p, out _);
        }

        try
        {
            return await index.IndexPathsAsync(root, batch, ct);
        }
        catch
        {
            Enqueue(root, batch);
            throw;
        }
    }
}
