namespace TokenSaver.LogHelper.Models;

public enum LogSeverity
{
    Unknown = 0,
    Trace = 1,
    Debug = 2,
    Information = 3,
    Warning = 4,
    Error = 5,
    Critical = 6,
}

/// <summary>One logical log entry after folding continuation lines (stack traces, indented messages).</summary>
/// <summary>
/// One logical log entry after folding continuation lines. <see cref="Fields"/> holds every parsed key
/// (case-insensitive, nested keys dotted) when a structured format was applied; null for plain text.
/// </summary>
public sealed record LogEntry(
    DateTimeOffset? Timestamp,
    LogSeverity Severity,
    string? Category,
    string Message,
    string Raw,
    bool FirstChanceException = false,
    bool Truncated = false,
    IReadOnlyDictionary<string, string>? Fields = null);

/// <summary>A <c>key=value</c> term matched against <see cref="LogEntry.Fields"/> (key and value compare case-insensitively).</summary>
public sealed record FieldTerm(string Key, string Value);

/// <summary>Filter applied to entries. All fields are optional; null means "no constraint".</summary>
public sealed record LogFilterSpec
{
    public string? Match { get; init; }

    public string? Exclude { get; init; }

    public LogSeverity? MinLevel { get; init; }

    public string? Category { get; init; }

    public IReadOnlyList<FieldTerm> FieldTerms { get; init; } = Array.Empty<FieldTerm>();
}

/// <summary>
/// How to turn lines into structured entries. <c>Name</c> is auto, plain, json, xml, logfmt or regex.
/// <c>Pattern</c> is a regex with named groups (regex only). <c>Schema</c> is an optional JSON object
/// mapping standard slots (timestamp, level, message, category, exception) to source field names.
/// </summary>
public sealed record LogFormatSpec(string? Name, string? Pattern = null, string? Schema = null);

/// <summary>Who is asking. Carried into every facade call so hosts can scope and audit later.</summary>
public sealed record LogRequestContext(string CallerId, bool ReadOnly = true);

public sealed record LogQueryRequest(
    ChannelId Channel,
    LogFilterSpec? Filter = null,
    string View = "summary",
    string? Since = null,
    int WaitSeconds = 0,
    int MaxTokens = 1500,
    LogFormatSpec? Format = null);

public sealed record LogQueryResult(
    bool Ok,
    string? Error,
    string Text,
    int RawChars,
    int ReturnedChars,
    string? NextCursor,
    bool Truncated);

public sealed record LogChannelInfo(ChannelId Id, string Description, long? SizeBytes);

/// <summary>One read from a source. <c>Reset</c> is true when the cursor no longer matched and reading restarted.</summary>
public sealed record LogReadBatch(
    IReadOnlyList<LogEntry> Entries,
    string NextCursor,
    bool Reset,
    int Dropped);

public sealed class LogHelperOptions
{
    /// <summary>Directories a file source may read from. Empty means nothing is readable.</summary>
    public IReadOnlyList<string> AllowedRoots { get; set; } = Array.Empty<string>();

    /// <summary>Where run snapshots and other state are written.</summary>
    public string? StateDirectory { get; set; }

    public bool RedactionEnabled { get; set; } = true;

    /// <summary>Event Log channels that may be read. Security is never included by default.</summary>
    public IReadOnlyList<string> EventLogAllowList { get; set; } = new[] { "Application", "System" };

    public int MaxRegexLength { get; set; } = 512;

    public int MaxEntryBytes { get; set; } = 16_384;
}
