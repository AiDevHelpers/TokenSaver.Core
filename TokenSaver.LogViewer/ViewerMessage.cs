using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenSaver.LogViewer;

/// <summary>
/// One line on the viewer pipe, as JSON. Server to viewer: <c>hello</c> (channel, filter, title), <c>entry</c>,
/// <c>note</c> and <c>close</c>. Viewer to server: <c>ready</c> with the viewer's process id.
/// </summary>
public sealed record ViewerMessage
{
    public const string Hello = "hello";
    public const string Entry = "entry";
    public const string Note = "note";
    public const string Close = "close";
    public const string Ready = "ready";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [JsonPropertyName("t")]
    public string Type { get; init; } = Note;

    public string? Text { get; init; }

    public string? Severity { get; init; }

    public string? Category { get; init; }

    public DateTimeOffset? Ts { get; init; }

    /// <summary>For entries: true when the entry passes the filter the AI asked with. Always true with no filter.</summary>
    public bool? Matched { get; init; }

    public int? Pid { get; init; }

    public string? Channel { get; init; }

    public string? Filter { get; init; }

    public string ToLine() => JsonSerializer.Serialize(this, Json);

    public static bool TryParse(string? line, out ViewerMessage? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            message = JsonSerializer.Deserialize<ViewerMessage>(line, Json);
            return message is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Name of the pipe one watch is served on.</summary>
    public static string PipeNameFor(string watchId) => $"tokensaver-logview-{watchId}";
}
