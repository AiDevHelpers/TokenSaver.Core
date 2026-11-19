using TokenSaver.ContextHelper.Configuration;

using System.Collections.ObjectModel;

namespace TokenSaver.Common.Core.Configuration;

/// <summary>
///     One configured Ollama endpoint. <see cref="Name" /> is used to select it explicitly
///     (via the <c>endpointName</c> parameter on tools like CleanAndChat) or to identify it
///     in <see cref="OllamaEndpointRegistry" /> health reporting.
/// </summary>
public class OllamaEndpointConfig
{
    public string Name { get; set; } = "";

    // CA1056 suppressed: Url is bound directly from JSON config (appsettings.json /
    // SimpleModelServer.config.json) via IOptions<T>, and every call site treats it as a plain string
    // for concatenation (e.g. $"{Url}/api/tags"). Changing to System.Uri would need config
    // binder validation for every deployment's config file plus touching call sites across
    // EndpointHealthMonitor/EndpointRoutingHandler/OllamaEndpointRegistry — not worth the risk
    // for a naming-convention rule.
#pragma warning disable CA1056
    public string Url { get; set; } = "";
#pragma warning restore CA1056
    public bool Primary { get; set; }
    public Collection<string> Models { get; set; } = [];
}

/// <summary>
///     Strategy used by <see cref="OllamaEndpointRegistry" /> to pick an endpoint when the
///     caller doesn't request one explicitly.
/// </summary>
public enum EndpointSelectStrategy
{
    /// <summary>Use the primary endpoint; switch to the next healthy endpoint on failure.</summary>
    Failover,

    /// <summary>Distribute requests round-robin across all healthy endpoints.</summary>
    RoundRobin,

    /// <summary>Only ever use the endpoint named explicitly by the caller, falling back to primary.</summary>
    Explicit
}

public class TokenSaverConfig
{
    public const string SectionName = "TokenSaver";

    // CA1056 suppressed: same reasoning as OllamaEndpointConfig.Url above — config-bound string,
    // used as a plain string at every call site.
#pragma warning disable CA1056
    public string OllamaUrl { get; set; } = "http://localhost:11435";
#pragma warning restore CA1056
    public string OllamaModel { get; set; } = "HelperModel";
    public int ServerPort { get; set; } = 11436;
    public int ContextSizeLimit { get; set; } = 120_000;
    /// <summary>
    ///     When true, the console log minimum level is Debug instead of Information (see
    ///     <c>Program.cs</c>, read directly off configuration before <see cref="TokenSaverConfig" />
    ///     is bound). Off by default; <c>scripts/run-dev-server.ps1</c> turns this on in the
    ///     dev-appsettings.json it seeds.
    /// </summary>
    public bool VerboseLogging { get; set; }

    /// <summary>
    ///     Optional additional Ollama endpoints beyond the default <see cref="OllamaUrl" />.
    ///     When empty (the default), <see cref="OllamaEndpointRegistry" /> synthesizes a single
    ///     endpoint from <see cref="OllamaUrl" />/<see cref="OllamaModel" />, preserving today's
    ///     single-endpoint behavior unchanged. When populated, list at least one entry with
    ///     <see cref="OllamaEndpointConfig.Primary" /> set to true.
    /// </summary>
    public Collection<OllamaEndpointConfig> Endpoints { get; set; } = [];

    /// <summary>Strategy for automatic endpoint selection. Default: failover.</summary>
    public EndpointSelectStrategy EndpointSelectStrategy { get; set; } = EndpointSelectStrategy.Failover;

    /// <summary>How often <c>EndpointHealthMonitor</c> pings each configured endpoint.</summary>
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    private string _configuredRoot = "";
    private string _lastResolvedRoot = "";

    /// <summary>
    ///     Workspace root path. Setting it records the configured default (appsettings or environment).
    ///     Reading it answers for the request being served: that session's resolved root, else the
    ///     configured default — never another client's root. Outside a request (background services)
    ///     it answers the most recently resolved root, else the configured default.
    ///     When set, this is exported as SIMPLEMODELSERVER_WORKSPACE so that SimpleModelServer writes
    ///     its config and .tokensaver/ folder under this path.
    /// </summary>
    public string WorkspaceRoot
    {
        get
        {
            WorkspaceScope? scope = WorkspaceScope.Current;
            if (scope is not null)
            {
                return string.IsNullOrWhiteSpace(scope.Root) ? _configuredRoot : scope.Root;
            }

            return string.IsNullOrWhiteSpace(_lastResolvedRoot) ? _configuredRoot : _lastResolvedRoot;
        }
        set => _configuredRoot = value ?? "";
    }

    /// <summary>
    ///     Executables the <c>run_tests</c> and <c>lint_fix</c> tools may launch, comma separated. A command whose
    ///     executable is not listed (or is given as a path) is refused. Empty means the built-in default list.
    /// </summary>
    public string AllowedCommands { get; set; } = "";

    /// <summary>
    ///     When true (default) every /mcp and /api request must carry the per-install bearer token. The Host and
    ///     Origin checks apply either way.
    /// </summary>
    public bool RequireAuth { get; set; } = true;

    /// <summary>Token file path. Empty means %ProgramData%\TokenSaver\auth-token.</summary>
    public string AuthTokenPath { get; set; } = "";

    /// <summary>Local group whose members may read the token file.</summary>
    public string AuthGroupName { get; set; } = "TokenSaver Users";

    /// <summary>Records the latest root a session resolved, for callers that run outside any request.</summary>
    public void RecordResolvedRoot(string root)
    {
        _lastResolvedRoot = root;
    }

    /// <summary>
    ///     When true (default), the MCP server automatically registers itself with all detected
    ///     AI provider configs (Continue, Claude, Cursor, etc.) once SimpleModelServer is fully warm
    ///     (models loaded in VRAM). Set to false to disable auto-registration entirely.
    /// </summary>
    public bool AutoRegisterProviders { get; set; } = true;

    /// <summary>
    ///     Optional override for the per-workspace behavior config file (defaults to
    ///     <c>{workspaceRoot}/.tokensaver/config.json</c> — see
    ///     <c>TokenSaver.Config.BehaviorConfigService.LocalConfigPath</c>). When set, exported as
    ///     <c>TOKENSAVER_LOCAL_CONFIG_PATH</c> so <c>BehaviorConfigService</c> reads/writes that file
    ///     instead. Used by <c>scripts/run-dev-server.ps1</c> to point the dev loop at
    ///     <c>.tokensaver/dev-config.json</c> without touching the real one.
    /// </summary>
    public string BehaviorConfigPath { get; set; } = "";

    /// <summary>Context hygiene pipeline configuration.</summary>
    public ContextHelperConfig ContextHelper { get; set; } = new();

    /// <summary>Token-economy tool surface configuration (distillation, terse-mode, semantic index, savings tracking).</summary>
    public TokenEconomyConfig TokenEconomy { get; set; } = new();

}
