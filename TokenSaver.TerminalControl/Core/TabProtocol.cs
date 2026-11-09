namespace TokenSaver.TerminalControl.Core;

/// <summary>
/// Line protocol shared with Scripts/tokensaver-tab.ps1.
/// Commands go in as "&lt;id&gt; &lt;command&gt;"; the tab writes "__TOKENSAVER_END__ &lt;id&gt; &lt;exitcode&gt;" after each command.
/// </summary>
public static class TabProtocol
{
    public const string EndMarker = "__TOKENSAVER_END__";
    public const string CloseCommand = "__TOKENSAVER_CLOSE__";

    public static string FormatCommand(string requestId, string command)
    {
        if (command.Contains('\n') || command.Contains('\r'))
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
