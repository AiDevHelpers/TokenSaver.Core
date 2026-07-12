using TokenSaver.Common.Core.Configuration;
using TokenSaver.Common.Core.Contracts;
using TokenSaver.Database.Context;
using TokenSaver.Library.Models;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Text.Encodings.Web;
using System.Text.Json;

namespace TokenSaver.CodeHelper.Indexing;

/// <summary>
///     Workspace root/path resolution and the plain-JSON preferences file
///     (.tokensaver/preferences.json) — unrelated to the indexing schema.
/// </summary>
public sealed partial class WorkspaceConfigManager : IWorkspaceConfigManager
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load preferences.json; using defaults")]
    private static partial void LogPreferencesLoadFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WorkspaceRoot not configured — falling back to CWD '{Cwd}'. Set TOKENSAVER_TokenSaver__WorkspaceRoot to fix .tokensaver placement.")]
    private static partial void LogWorkspaceRootFallback(ILogger logger, string cwd);

    private const string PreferencesFile = "preferences.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly TokenSaverConfig _config;
    private readonly ILogger<WorkspaceConfigManager> _logger;

    public WorkspaceConfigManager(IOptions<TokenSaverConfig> config, ILogger<WorkspaceConfigManager> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public string GetWorkspaceRoot()
    {
        if (!string.IsNullOrWhiteSpace(_config.WorkspaceRoot))
        {
            return _config.WorkspaceRoot;
        }

        string? env = Environment.GetEnvironmentVariable("TOKENSAVER_WorkspaceRoot");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        string cwd = Directory.GetCurrentDirectory();
        LogWorkspaceRootFallback(_logger, cwd);
        return cwd;
    }

    public string GetLocalMcpRoot(string? workspaceRoot = null)
    {
        return Path.GetFullPath(Path.Combine(workspaceRoot ?? GetWorkspaceRoot(),
            WorkspaceIndexContextFactory.TokenSaverFolderName));
    }

    public async Task<WorkspacePreferences> LoadPreferencesAsync(string? workspaceRoot = null)
    {
        string path = Path.Combine(GetLocalMcpRoot(workspaceRoot), PreferencesFile);
        if (!File.Exists(path))
        {
            return new WorkspacePreferences();
        }

        try
        {
            string json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<WorkspacePreferences>(json) ?? new WorkspacePreferences();
        }
        catch (Exception ex)
        {
            LogPreferencesLoadFailed(_logger, ex);
            return new WorkspacePreferences();
        }
    }

    public async Task SavePreferencesAsync(WorkspacePreferences prefs, string? workspaceRoot = null)
    {
        string dir = GetLocalMcpRoot(workspaceRoot);
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, PreferencesFile);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(prefs, JsonOpts));
    }
}
