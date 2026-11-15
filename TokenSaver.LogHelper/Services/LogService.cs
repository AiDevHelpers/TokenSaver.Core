using System.Globalization;
using TokenSaver.LogHelper.Contracts;
using TokenSaver.LogHelper.Filtering;
using TokenSaver.LogHelper.Models;
using TokenSaver.LogHelper.State;

namespace TokenSaver.LogHelper.Services;

/// <summary>
/// The facade every host calls. Supports channel listing, summary queries (cursor or run diff), and run marking.
/// Sink comparison arrives in phase 6.
/// </summary>
public sealed class LogService : ILogService
{
    private const string LastRunToken = "lastRun";
    private const string RunIdPrefix = "run-";

    private readonly IReadOnlyList<ILogSourceProvider> _providers;
    private readonly LogHelperOptions _options;
    private readonly RunSnapshotStore? _runs;

    public LogService(IEnumerable<ILogSourceProvider> providers, LogHelperOptions options)
    {
        _providers = providers.ToList();
        _options = options;
        _runs = string.IsNullOrWhiteSpace(options.StateDirectory)
            ? null
            : new RunSnapshotStore(options.StateDirectory);
    }

    public async Task<IReadOnlyList<LogChannelInfo>> ListChannelsAsync(string? nameFilter, LogRequestContext context, CancellationToken ct)
    {
        var all = new List<LogChannelInfo>();
        foreach (ILogSourceProvider provider in _providers)
        {
            all.AddRange(await provider.ListChannelsAsync(ct));
        }

        if (string.IsNullOrEmpty(nameFilter))
        {
            return all;
        }

        return all
            .Where(c => c.Id.ToString().Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task<LogQueryResult> QueryAsync(LogQueryRequest request, LogRequestContext context, CancellationToken ct)
    {
        if (!string.Equals(request.View, "summary", StringComparison.Ordinal))
        {
            return Fail($"View '{request.View}' is not available yet; use 'summary'.");
        }

        if (IsRunDiffRequest(request.Since))
        {
            return await QueryAgainstRunAsync(request, ct);
        }

        LogReadResult read = await ReadAllAsync(request.Channel, request.Since, ct);
        if (read.Error is not null)
        {
            return Fail(read.Error);
        }

        LogReadBatch batch = read.Batch!;
        LogFilterResult filtered = LogFilter.Apply(batch.Entries, request.Filter ?? new LogFilterSpec(), _options);
        if (filtered.Error is not null)
        {
            return Fail(filtered.Error);
        }

        IReadOnlyList<TemplateRow> rows = TemplateAggregator.Aggregate(filtered.Kept, _options);
        SummaryText summary = SummaryFormatter.Format(rows, request.MaxTokens);

        string text = batch.Reset
            ? "(cursor reset: source changed)\n" + summary.Text
            : summary.Text;

        return Ok(text, RawCharsOf(batch.Entries), batch.NextCursor, summary.Truncated);
    }

    public async Task<LogQueryResult> MarkRunAsync(ChannelId channel, LogRequestContext context, CancellationToken ct)
    {
        if (_runs is null)
        {
            return Fail("No state directory is configured, so runs cannot be marked.");
        }

        LogReadResult read = await ReadAllAsync(channel, null, ct);
        if (read.Error is not null)
        {
            return Fail(read.Error);
        }

        IReadOnlyList<LogEntry> entries = read.Batch!.Entries;
        IReadOnlyList<TemplateRow> rows = TemplateAggregator.Aggregate(entries, _options);
        string runId = NewRunId();
        var snapshot = RunSnapshot.FromRows(runId, DateTimeOffset.UtcNow, rows);

        await _runs.AppendAsync(channel.ToString(), snapshot, ct);

        return Ok(
            string.Create(CultureInfo.InvariantCulture, $"{runId} marked: {rows.Count} templates from {entries.Count} entries"),
            RawCharsOf(entries),
            nextCursor: null,
            truncated: false);
    }

    public Task<LogQueryResult> SinkCompareAsync(ChannelId appChannel, ChannelId sinkChannel, int maxTokens, LogRequestContext context, CancellationToken ct) =>
        throw new NotSupportedException("Sink comparison arrives in phase 6.");

    private static bool IsRunDiffRequest(string? since) =>
        since is not null
        && (string.Equals(since, LastRunToken, StringComparison.Ordinal)
            || since.StartsWith(RunIdPrefix, StringComparison.Ordinal));

    private async Task<LogQueryResult> QueryAgainstRunAsync(LogQueryRequest request, CancellationToken ct)
    {
        if (_runs is null)
        {
            return Fail("No state directory is configured, so runs cannot be compared.");
        }

        IReadOnlyList<RunSnapshot> stored = await _runs.ListAsync(request.Channel.ToString(), ct);
        RunSnapshot? baseline;
        if (string.Equals(request.Since, LastRunToken, StringComparison.Ordinal))
        {
            baseline = stored.LastOrDefault();
            if (baseline is null)
            {
                return Ok("no baseline for this channel yet; call LogMarkRun first.", 0, null, false);
            }
        }
        else
        {
            baseline = stored.FirstOrDefault(s => string.Equals(s.RunId, request.Since, StringComparison.Ordinal));
            if (baseline is null)
            {
                return Fail($"unknown run '{request.Since}' for this channel.");
            }
        }

        LogReadResult read = await ReadAllAsync(request.Channel, null, ct);
        if (read.Error is not null)
        {
            return Fail(read.Error);
        }

        IReadOnlyList<LogEntry> entries = read.Batch!.Entries;
        var current = RunSnapshot.FromRows("current", DateTimeOffset.UtcNow, TemplateAggregator.Aggregate(entries, _options));
        RunDiff diff = RunDiffer.Diff(baseline, current);

        if (diff.BaselineReset)
        {
            return Ok("baseline reset: masking rules changed since the last run; call LogMarkRun to set a new baseline.",
                RawCharsOf(entries), null, false);
        }

        var candidates = new List<(TemplateRow Row, LogEntry Entry)>();
        foreach (TemplateChange change in diff.New)
        {
            candidates.Add(ToCandidate(change.Template, change.Severity, change.CurrentCount, change.Sample, null));
        }

        foreach (TemplateChange change in diff.Changed)
        {
            candidates.Add(ToCandidate(change.Template, change.Severity, change.CurrentCount, change.Sample, change.PreviousCount));
        }

        LogFilterResult filtered = LogFilter.Apply(candidates.Select(c => c.Entry), request.Filter ?? new LogFilterSpec(), _options);
        if (filtered.Error is not null)
        {
            return Fail(filtered.Error);
        }

        var kept = new HashSet<LogEntry>(filtered.Kept, ReferenceEqualityComparer.Instance);
        IReadOnlyList<TemplateRow> rows = candidates.Where(c => kept.Contains(c.Entry)).Select(c => c.Row).ToList();

        SummaryText summary = SummaryFormatter.Format(rows, request.MaxTokens);
        var text = new System.Text.StringBuilder();
        text.Append(rows.Count == 0 ? "no new or changed messages since " + baseline.RunId : summary.Text);
        if (diff.Gone.Count > 0)
        {
            text.Append('\n').Append(string.Create(CultureInfo.InvariantCulture, $"{diff.Gone.Count} template(s) no longer present."));
        }

        return Ok(text.ToString(), RawCharsOf(entries), null, summary.Truncated);
    }

    private static (TemplateRow Row, LogEntry Entry) ToCandidate(string template, LogSeverity severity, int count, string sample, int? previousCount)
    {
        string label = previousCount is int prev
            ? string.Create(CultureInfo.InvariantCulture, $"{template} (was {prev})")
            : template;
        var row = new TemplateRow(string.Empty, label, severity, count, null, null, sample);
        var entry = new LogEntry(null, severity, null, template, sample);
        return (row, entry);
    }

    private async Task<LogReadResult> ReadAllAsync(ChannelId channel, string? cursor, CancellationToken ct)
    {
        ILogSourceProvider? provider = _providers.FirstOrDefault(p =>
            string.Equals(p.Scheme, channel.Scheme, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return LogReadResult.Failed($"No log provider is registered for scheme '{channel.Scheme}'.");
        }

        try
        {
            LogReadBatch batch = await provider.Open(channel).ReadAsync(cursor, int.MaxValue, ct);
            return LogReadResult.Ok(batch);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return LogReadResult.Failed(ex.Message);
        }
    }

    private static string NewRunId() =>
        RunIdPrefix + Guid.NewGuid().ToString("N")[..12];

    private static int RawCharsOf(IEnumerable<LogEntry> entries) =>
        entries.Sum(e => e.Raw.Length + 1);

    private static LogQueryResult Ok(string text, int rawChars, string? nextCursor, bool truncated) =>
        new(Ok: true, Error: null, Text: text, RawChars: rawChars, ReturnedChars: text.Length, NextCursor: nextCursor, Truncated: truncated);

    private static LogQueryResult Fail(string error) =>
        new(Ok: false, Error: error, Text: string.Empty, RawChars: 0, ReturnedChars: 0, NextCursor: null, Truncated: false);

    private sealed record LogReadResult(LogReadBatch? Batch, string? Error)
    {
        public static LogReadResult Ok(LogReadBatch batch) => new(batch, null);

        public static LogReadResult Failed(string error) => new(null, error);
    }
}
