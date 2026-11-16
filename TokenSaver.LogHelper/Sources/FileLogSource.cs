using System.Text.Json;
using TokenSaver.LogHelper.Contracts;
using TokenSaver.LogHelper.Models;
using TokenSaver.LogHelper.Parsing;
using TokenSaver.LogHelper.Security;

namespace TokenSaver.LogHelper.Sources;

/// <summary>
/// Reads one log file incrementally. Only complete lines are consumed; a partial trailing line is left
/// for the next read. A cursor whose anchor no longer matches (truncation or rewrite) restarts from the
/// beginning and sets <see cref="LogReadBatch.Reset"/>. <c>maxEntries</c> is not enforced here; callers cap output.
/// </summary>
public sealed class FileLogSource : ILogSource
{
    private const int MaxChunkBytes = 8 * 1024 * 1024;

    private readonly string _path;
    private readonly LogHelperOptions _options;

    public FileLogSource(string path, LogHelperOptions options)
    {
        _path = path;
        _options = options;
    }

    public Task<LogReadBatch> ReadAsync(string? cursor, int maxEntries, CancellationToken ct) =>
        ReadAsync(cursor, maxEntries, null, ct);

    public async Task<LogReadBatch> ReadAsync(string? cursor, int maxEntries, LogFormatSpec? format, CancellationToken ct)
    {
        if (!PathGuard.TryResolve(_path, _options.AllowedRoots, out string? canonical, out string? reason) || canonical is null)
        {
            throw new UnauthorizedAccessException(reason);
        }

        if (!File.Exists(canonical))
        {
            return Empty(FileCursorState.Encode(new FileCursorState(canonical, 0, string.Empty)), reset: false);
        }

        using var stream = new FileStream(
            canonical,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);

        long length = stream.Length;
        TextLayout layout = await ReadLayoutAsync(stream, length, ct);

        long offset = layout.BomLength;
        bool reset = false;
        string? storedFormat = null;

        if (FileCursorState.TryDecode(cursor, out var state) && state is not null && state.Path == canonical)
        {
            if (state.Offset > length || !string.Equals(await AnchorAsync(stream, state.Offset, ct), state.Anchor, StringComparison.Ordinal))
            {
                reset = true;
            }
            else
            {
                offset = Math.Max(state.Offset, layout.BomLength);
                storedFormat = state.Format;
            }
        }

        // An explicit format from the caller wins. Otherwise the format stored in the cursor is reused, so a
        // channel is sniffed once and then stays on the same format until the cursor resets.
        LogFormatSpec? explicitSpec = format is not null && StructuredParser.NameOf(format) != "auto" ? format : null;
        string? carriedFormat = explicitSpec is not null ? JsonSerializer.Serialize(explicitSpec) : (reset ? null : storedFormat);

        long available = length - offset;
        if (available <= 0)
        {
            return Empty(FileCursorState.Encode(new FileCursorState(canonical, offset, await AnchorAsync(stream, offset, ct), carriedFormat)), reset);
        }

        int toRead = (int)Math.Min(available, MaxChunkBytes);
        byte[] buffer = new byte[toRead];
        await ReadExactlyAsync(stream, offset, buffer, ct);

        int consumed = EncodingSniffer.LastCompleteLineEnd(buffer, toRead, layout.Newline);
        if (consumed == 0)
        {
            return Empty(FileCursorState.Encode(new FileCursorState(canonical, offset, await AnchorAsync(stream, offset, ct), carriedFormat)), reset);
        }

        string text = layout.Encoding.GetString(buffer, 0, consumed);
        // The consumed text always ends with '\n', so the final split element is empty and is dropped.
        string[] parts = text.Split('\n');
        var lines = parts.Take(parts.Length - 1).Select(l => l.TrimEnd('\r')).ToList();

        LogFormatSpec spec = explicitSpec ?? ReadSpec(carriedFormat) ?? FormatSniffer.Resolve(format, lines, _options);
        IReadOnlyList<LogEntry> entries = ParseWith(spec, lines);

        long newOffset = offset + consumed;
        string anchor = await AnchorAsync(stream, newOffset, ct);

        return new LogReadBatch(
            entries,
            FileCursorState.Encode(new FileCursorState(canonical, newOffset, anchor, JsonSerializer.Serialize(spec))),
            reset,
            Dropped: 0);
    }

    private IReadOnlyList<LogEntry> ParseWith(LogFormatSpec spec, List<string> lines)
    {
        if (StructuredParser.NameOf(spec) == "plain")
        {
            return new LogParser(_options).Parse(lines);
        }

        if (!StructuredParser.TryCreate(spec, _options, out StructuredParser? parser, out string? error) || parser is null)
        {
            throw new ArgumentException(error, nameof(spec));
        }

        return parser.ParseLines(lines);
    }

    private static LogFormatSpec? ReadSpec(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LogFormatSpec>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LogReadBatch Empty(string cursor, bool reset) =>
        new(Array.Empty<LogEntry>(), cursor, reset, 0);

    private static async Task<TextLayout> ReadLayoutAsync(FileStream stream, long length, CancellationToken ct)
    {
        int headLength = (int)Math.Min(4, length);
        byte[] head = new byte[headLength];
        await ReadExactlyAsync(stream, 0, head, ct);
        return EncodingSniffer.Sniff(head);
    }

    private static async Task<string> AnchorAsync(FileStream stream, long offset, CancellationToken ct)
    {
        int count = (int)Math.Min(FileCursorState.AnchorBytes, offset);
        if (count <= 0)
        {
            return string.Empty;
        }

        byte[] bytes = new byte[count];
        await ReadExactlyAsync(stream, offset - count, bytes, ct);
        return FileCursorState.ComputeAnchor(bytes);
    }

    private static async Task ReadExactlyAsync(FileStream stream, long position, byte[] buffer, CancellationToken ct)
    {
        stream.Position = position;
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
            {
                break;
            }

            read += n;
        }
    }
}
