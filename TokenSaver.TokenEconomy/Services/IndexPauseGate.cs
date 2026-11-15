using System.Collections.Concurrent;

namespace TokenSaver.TokenEconomy.Services;

/// <summary>
///     Pause control for the semantic index run, per workspace root. A paused run stops at the next
///     file boundary (the file in progress finishes), keeps the chunks it already stored, and continues
///     when resumed. Shared by the indexer (which waits) and the trigger/tools (which pause and report).
/// </summary>
public sealed class IndexPauseGate
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _paused = new(StringComparer.OrdinalIgnoreCase);

    public bool IsPaused(string root) => _paused.ContainsKey(root);

    public void Pause(string root) =>
        _ = _paused.TryAdd(root, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public void Resume(string root)
    {
        if (_paused.TryRemove(root, out TaskCompletionSource? gate))
        {
            _ = gate.TrySetResult();
        }
    }

    /// <summary>Returns immediately unless the root is paused; then waits until resumed (or cancelled).</summary>
    public async Task WaitIfPausedAsync(string root, CancellationToken ct)
    {
        if (_paused.TryGetValue(root, out TaskCompletionSource? gate))
        {
            await gate.Task.WaitAsync(ct);
        }
    }
}
