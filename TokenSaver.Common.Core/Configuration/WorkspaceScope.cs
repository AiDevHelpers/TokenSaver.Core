namespace TokenSaver.Common.Core.Configuration;

/// <summary>
///     The workspace root of the request currently being served. One instance is created per HTTP
///     request and flows with the async call chain (<see cref="Current" />), so the shared
///     <see cref="TokenSaverConfig" /> singleton can answer <see cref="TokenSaverConfig.WorkspaceRoot" />
///     per session instead of holding one global value that every client overwrites.
/// </summary>
public sealed class WorkspaceScope
{
    private static readonly AsyncLocal<WorkspaceScope?> s_current = new();

    /// <summary>The scope of the request being served, or null outside a request (background services, tests).</summary>
    public static WorkspaceScope? Current => s_current.Value;

    /// <summary>The root known for this request's MCP session; null until the session's roots are resolved.</summary>
    public string? Root { get; set; }

    /// <summary>Makes a new scope current for the calling async flow and everything it awaits.</summary>
    public static WorkspaceScope Begin(string? root)
    {
        WorkspaceScope scope = new() { Root = root };
        s_current.Value = scope;
        return scope;
    }
}
