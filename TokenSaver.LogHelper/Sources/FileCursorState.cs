using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TokenSaver.LogHelper.Models;

namespace TokenSaver.LogHelper.Sources;

/// <summary>
/// Resume state for a file source. <see cref="Anchor"/> is a hash of the bytes just before
/// <see cref="Offset"/>; if the bytes there changed (truncation, rewrite) the cursor no longer matches.
/// <see cref="Format"/> is the resolved <see cref="LogFormatSpec"/> as JSON, so a channel is sniffed once.
/// </summary>
internal sealed record FileCursorState(string Path, long Offset, string Anchor, string? Format = null)
{
    public const int AnchorBytes = 256;

    public static string Encode(FileCursorState state) =>
        LogCursor.Encode(JsonSerializer.Serialize(state));

    public static bool TryDecode(string? token, out FileCursorState? state)
    {
        state = null;
        if (!LogCursor.TryDecode(token, out var payload))
        {
            return false;
        }

        try
        {
            state = JsonSerializer.Deserialize<FileCursorState>(payload);
            return state is not null && state.Offset >= 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string ComputeAnchor(byte[] bytesBeforeOffset)
    {
        if (bytesBeforeOffset.Length == 0)
        {
            return string.Empty;
        }

        byte[] digest = SHA256.HashData(bytesBeforeOffset);
        return Convert.ToHexString(digest, 0, 16).ToLowerInvariant();
    }
}
