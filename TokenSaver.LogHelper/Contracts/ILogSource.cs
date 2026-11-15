using TokenSaver.LogHelper.Models;

namespace TokenSaver.LogHelper.Contracts;

/// <summary>Reads entries from one channel. Cursors are opaque to callers.</summary>
public interface ILogSource
{
    Task<LogReadBatch> ReadAsync(string? cursor, int maxEntries, CancellationToken ct);
}

/// <summary>Lists and opens channels for one URI scheme.</summary>
public interface ILogSourceProvider
{
    string Scheme { get; }

    Task<IReadOnlyList<LogChannelInfo>> ListChannelsAsync(CancellationToken ct);

    ILogSource Open(ChannelId id);
}
