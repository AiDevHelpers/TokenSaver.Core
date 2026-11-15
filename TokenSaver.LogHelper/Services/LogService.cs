using TokenSaver.LogHelper.Contracts;
using TokenSaver.LogHelper.Filtering;
using TokenSaver.LogHelper.Models;

namespace TokenSaver.LogHelper.Services;

/// <summary>
/// The facade every host calls. Phase 1 supports channel listing and the summary view.
/// Run marking and sink comparison arrive in later phases.
/// </summary>
public sealed class LogService : ILogService
{
    private readonly IReadOnlyList<ILogSourceProvider> _providers;
    private readonly LogHelperOptions _options;

    public LogService(IEnumerable<ILogSourceProvider> providers, LogHelperOptions options)
    {
        _providers = providers.ToList();
        _options = options;
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

        ILogSourceProvider? provider = _providers.FirstOrDefault(p =>
            string.Equals(p.Scheme, request.Channel.Scheme, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return Fail($"No log provider is registered for scheme '{request.Channel.Scheme}'.");
        }

        LogReadBatch batch;
        try
        {
            batch = await provider.Open(request.Channel).ReadAsync(request.Since, int.MaxValue, ct);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return Fail(ex.Message);
        }

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

        // Each entry's raw text plus its line terminator, so RawChars matches what was read.
        int rawChars = batch.Entries.Sum(e => e.Raw.Length + 1);

        return new LogQueryResult(
            Ok: true,
            Error: null,
            Text: text,
            RawChars: rawChars,
            ReturnedChars: text.Length,
            NextCursor: batch.NextCursor,
            Truncated: summary.Truncated);
    }

    public Task<LogQueryResult> MarkRunAsync(ChannelId channel, LogRequestContext context, CancellationToken ct) =>
        throw new NotSupportedException("Run marking arrives in phase 2.");

    public Task<LogQueryResult> SinkCompareAsync(ChannelId appChannel, ChannelId sinkChannel, int maxTokens, LogRequestContext context, CancellationToken ct) =>
        throw new NotSupportedException("Sink comparison arrives in phase 6.");

    private static LogQueryResult Fail(string error) =>
        new(Ok: false, Error: error, Text: string.Empty, RawChars: 0, ReturnedChars: 0, NextCursor: null, Truncated: false);
}
