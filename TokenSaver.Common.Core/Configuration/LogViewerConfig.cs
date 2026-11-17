namespace TokenSaver.Common.Core.Configuration;

/// <summary>Settings for the log viewer tab (TokenSaver.LogViewer): a console that shows the log being watched.</summary>
public class LogViewerConfig
{
    /// <summary>
    ///     When true, every <c>LogQuery</c> on a file channel also opens a viewer tab for that channel, so the user sees
    ///     what the AI reads. A tab the user closed is not reopened by this. Off by default.
    /// </summary>
    public bool AutoOpenOnQuery { get; set; }

    /// <summary>Full path of <c>tokensaver-logview.exe</c>. Empty means the copy next to the MCP host.</summary>
    public string ViewerPath { get; set; } = "";
}
