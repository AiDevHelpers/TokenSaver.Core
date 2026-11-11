namespace TokenSaver.TerminalControl.Core;

/// <summary>
/// Line protocol shared with Scripts/tokensaver-tab.ps1.
/// Commands go in as "&lt;id&gt; &lt;command&gt;"; the tab writes "__TOKENSAVER_END__ &lt;id&gt; &lt;exitcode&gt;" after each command.
/// </summary>
public static class TabProtocol
{
    public const string EndMarker = "__TOKENSAVER_END__";
    public const string ErrorMarker = "__TOKENSAVER_ERR__";
    public const string CloseCommand = "__TOKENSAVER_CLOSE__";

    /// <summary>Parses "__TOKENSAVER_ERR__ &lt;id&gt; &lt;text&gt;" written in-band on the stdout pipe.</summary>
    public static bool TryParseError(string line, out string requestId, out string text)
    {
        requestId = string.Empty;
        text = string.Empty;

        if (!line.StartsWith(ErrorMarker + " ", StringComparison.Ordinal))
        {
            return false;
        }

        var rest = line[(ErrorMarker.Length + 1)..];
        var space = rest.IndexOf(" ", StringComparison.Ordinal);
        if (space < 1)
        {
            return false;
        }

        requestId = rest[..space];
        text = rest[(space + 1)..];
        return true;
    }

    public static string FormatCommand(string requestId, string command)
    {
        if (command.Contains("\n", StringComparison.Ordinal) || command.Contains("\r", StringComparison.Ordinal))
        {
            throw new ArgumentException("Commands must be a single line.", nameof(command));
        }

        return $"{requestId} {command}";
    }

    public static bool TryParseEnd(string line, out string requestId, out int exitCode)
    {
        requestId = string.Empty;
        exitCode = 0;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] != EndMarker)
        {
            return false;
        }

        if (!int.TryParse(parts[2], out exitCode))
        {
            return false;
        }

        requestId = parts[1];
        return true;
    }
}
