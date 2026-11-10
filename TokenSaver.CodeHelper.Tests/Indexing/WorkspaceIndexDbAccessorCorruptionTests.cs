using TokenSaver.CodeHelper.Indexing;
using TokenSaver.Database.Context;

using Microsoft.Extensions.Logging;

using Xunit;

namespace TokenSaver.CodeHelper.Tests.Indexing;

/// <summary>
///     Covers the corrupt-index branch of <see cref="WorkspaceIndexDbAccessor.GetDbAsync" />: a
///     non-SQLite file at the index path must be logged at Error with the SQLite error, then the
///     file is deleted and rebuilt. A healthy open must log nothing at Error.
/// </summary>
public sealed class WorkspaceIndexDbAccessorCorruptionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wida-corrupt-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup; SQLite handles can linger briefly on Windows
            }
        }
    }

    [Fact]
    public async Task GetDbAsync_CorruptIndexFile_LogsErrorWithExceptionAndRebuilds()
    {
        string dbPath = WorkspaceIndexContextFactory.GetDbPath(_root);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllText(dbPath, new string('x', 4096));
        ListLogger<WorkspaceIndexDbAccessor> logger = new();

        using WorkspaceIndexDbAccessor accessor = new(logger);
        WorkspaceIndexContext ctx = await accessor.GetDbAsync(_root, CancellationToken.None);

        Assert.NotNull(ctx);
        LogEntry entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("declared corrupt", entry.Message, StringComparison.Ordinal);
        Assert.Contains(_root, entry.Message, StringComparison.Ordinal);
        Assert.NotNull(entry.Exception);
    }

    [Fact]
    public async Task GetDbAsync_HealthyOpen_LogsNoError()
    {
        ListLogger<WorkspaceIndexDbAccessor> logger = new();

        using WorkspaceIndexDbAccessor accessor = new(logger);
        _ = await accessor.GetDbAsync(_root, CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }
}
