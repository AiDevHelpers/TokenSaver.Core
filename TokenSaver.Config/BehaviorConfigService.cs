using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace TokenSaver.Config;

/// <summary>
///     Loads, merges, watches, and persists <see cref="TokenSaverBehaviorConfig" /> from two
///     sources:
///     <list type="bullet">
///         <item><c>{workspaceRoot}/.tokensaver/config.json</c> — per-workspace (local).</item>
///         <item><c>%ProgramData%\TokenSaver\config.json</c> — machine-global.</item>
///     </list>
///     Both files are read at first access and re-read whenever either changes on disk (via
///     <see cref="FileSystemWatcher" />). On any value conflict the <b>local value always wins</b>
///     over the global one (see <see cref="TokenSaverBehaviorConfig.Merge" />). Registered as a
///     singleton; results are cached per workspace root.
/// </summary>
public sealed partial class BehaviorConfigService : IDisposable
{
    public const string ConfigFileName = "config.json";
    private const string LocalFolder = ".tokensaver";
    private const string GlobalFolder = "TokenSaver";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<BehaviorConfigService> _logger;
    private readonly ConcurrentDictionary<string, WatchedWorkspace> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _sync = new();
    private FileSystemWatcher? _globalWatcher;
    private bool _disposed;

    public BehaviorConfigService(ILogger<BehaviorConfigService> logger)
    {
        _logger = logger;
    }

    /// <summary>Raised after either config file changes on disk and the merged view is refreshed.</summary>
    public event EventHandler? ConfigChanged;

    /// <summary>Env var name that overrides the global config directory; tests use this to avoid touching the real machine-wide file.</summary>
    private const string GlobalDirOverrideEnvVar = "TOKENSAVER_GLOBAL_CONFIG_DIR";

    /// <summary>
    ///     Env var name that overrides the per-workspace config file path outright (bypassing
    ///     <paramref name="workspaceRoot" />-relative resolution entirely). Set from
    ///     <c>TokenSaverConfig.BehaviorConfigPath</c> at startup (see <c>Program.cs</c>), or directly
    ///     by <c>scripts/run-dev-server.ps1</c> to point the dev loop at
    ///     <c>.tokensaver/dev-config.json</c> instead of the real <c>.tokensaver/config.json</c>.
    /// </summary>
    private const string LocalPathOverrideEnvVar = "TOKENSAVER_LOCAL_CONFIG_PATH";

    /// <summary>The machine-global config path (<c>%ProgramData%\TokenSaver\config.json</c>, or <c>TOKENSAVER_GLOBAL_CONFIG_DIR</c> when set).</summary>
    public static string GlobalConfigPath =>
        Path.Combine(
            Environment.GetEnvironmentVariable(GlobalDirOverrideEnvVar) is { Length: > 0 } overrideDir
                ? overrideDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), GlobalFolder),
            ConfigFileName);

    /// <summary>
    ///     The per-workspace config path (<c>{workspaceRoot}/.tokensaver/config.json</c> by default,
    ///     or <see cref="LocalPathOverrideEnvVar" /> verbatim when set).
    /// </summary>
    public static string LocalConfigPath(string workspaceRoot) =>
        Environment.GetEnvironmentVariable(LocalPathOverrideEnvVar) is { Length: > 0 } overridePath
            ? overridePath
            : Path.Combine(workspaceRoot, LocalFolder, ConfigFileName);

    /// <summary>
    ///     Returns the merged (local-over-global) config for <paramref name="workspaceRoot" />,
    ///     wiring up file watchers on first access so later changes reload automatically.
    /// </summary>
    public TokenSaverBehaviorConfig GetConfig(string workspaceRoot)
    {
        string key = Normalize(workspaceRoot);
        WatchedWorkspace entry = _cache.GetOrAdd(key, k =>
        {
            WatchedWorkspace w = new(k);
            AttachLocalWatcher(w);
            EnsureGlobalWatcher();
            w.Merged = LoadMerged(k);
            return w;
        });
        return entry.Merged;
    }

    /// <summary>
    ///     Reads the config file for <paramref name="scope" />, applies <paramref name="mutate" />,
    ///     and writes it back (creating the folder/file as needed). Refreshes the cached merged
    ///     view and raises <see cref="ConfigChanged" />.
    /// </summary>
    public void Set(string workspaceRoot, ConfigScope scope, Func<TokenSaverBehaviorConfig, TokenSaverBehaviorConfig> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        string path = scope == ConfigScope.Local ? LocalConfigPath(workspaceRoot) : GlobalConfigPath;

        lock (_sync)
        {
            TokenSaverBehaviorConfig current = ReadFile(path) ?? TokenSaverBehaviorConfig.Defaults();
            TokenSaverBehaviorConfig updated = mutate(current);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(updated, WriteOptions));
        }

        // A global write may have created the folder for the first time — (re)attach its watcher.
        if (scope == ConfigScope.Global)
        {
            EnsureGlobalWatcher();
        }

        RefreshAll();
    }

    // ── Loading / merging ───────────────────────────────────────────────────────

    private TokenSaverBehaviorConfig LoadMerged(string workspaceRoot)
    {
        TokenSaverBehaviorConfig? local = ReadFile(LocalConfigPath(workspaceRoot));
        TokenSaverBehaviorConfig? global = ReadFile(GlobalConfigPath);
        return TokenSaverBehaviorConfig.Merge(local, global);
    }

    private TokenSaverBehaviorConfig? ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<TokenSaverBehaviorConfig>(json, ReadOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LogConfigReadFailed(path, ex);
            return null;
        }
    }

    private void RefreshAll()
    {
        foreach (WatchedWorkspace w in _cache.Values)
        {
            w.Merged = LoadMerged(w.Root);
        }

        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Watchers ────────────────────────────────────────────────────────────────

    private void AttachLocalWatcher(WatchedWorkspace w)
    {
        // Watch the actual resolved file (honors TOKENSAVER_LOCAL_CONFIG_PATH), not always
        // {root}/.tokensaver/config.json.
        string localPath = LocalConfigPath(w.Root);
        w.Watcher = TryCreateWatcher(Path.GetDirectoryName(localPath)!, Path.GetFileName(localPath));
    }

    private void EnsureGlobalWatcher()
    {
        lock (_sync)
        {
            if (_globalWatcher is not null)
            {
                return;
            }

            _globalWatcher = TryCreateWatcher(Path.GetDirectoryName(GlobalConfigPath)!, ConfigFileName);
        }
    }

    private FileSystemWatcher? TryCreateWatcher(string dir, string fileName)
    {
        if (!Directory.Exists(dir))
        {
            // Nothing to watch yet; a later Set() that creates the folder re-attaches.
            return null;
        }

        try
        {
            FileSystemWatcher watcher = new(dir, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Deleted += OnFileEvent;
            watcher.Renamed += OnFileEvent;
            return watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            LogWatcherFailed(dir, ex);
            return null;
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // FileSystemWatcher fires multiple events per save; RefreshAll is idempotent and cheap.
        RefreshAll();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static string Normalize(string workspaceRoot) => Path.GetFullPath(workspaceRoot);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _globalWatcher?.Dispose();
        foreach (WatchedWorkspace w in _cache.Values)
        {
            w.Watcher?.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read behavior config at {Path}; ignoring it")]
    private partial void LogConfigReadFailed(string path, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not watch config folder {Dir} for changes")]
    private partial void LogWatcherFailed(string dir, Exception ex);

    private sealed class WatchedWorkspace(string root)
    {
        public string Root { get; } = root;
        public volatile TokenSaverBehaviorConfig Merged = new();
        public FileSystemWatcher? Watcher { get; set; }
    }
}
