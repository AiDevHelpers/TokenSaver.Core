using System;
using System.IO;
using System.Text.Json;
using TokenSaver.Config;
using TokenSaver.Config.Contracts;

namespace TokenSaver.Client.Helpers.Configuration;

/// <summary>
/// Manages reading, merging, and writing behavior configuration from local and global files.
/// Mirrors the .NET-side BehaviorConfigService for use in client extensions.
/// </summary>
public static class BehaviorConfigManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Reads a behavior config from the specified file path.</summary>
    /// <returns>Parsed config or null if file doesn't exist or is empty.</returns>
    public static TokenSaverBehaviorConfig? ReadConfigFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            var text = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            return JsonSerializer.Deserialize<TokenSaverBehaviorConfig>(text, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes a behavior config to the specified file path, creating directories as needed.</summary>
    public static void WriteConfigFile(string filePath, TokenSaverBehaviorConfig config)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (dir != null)
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(filePath, json + "\n");
    }

    /// <summary>
    /// Merges local (workspace) and global (machine-wide) config files, with local taking precedence.
    /// </summary>
    public static RepoHelperBehaviorConfig GetMergedRepoHelperConfig(string workspaceRoot)
    {
        var local = ReadConfigFile(GetLocalConfigPath(workspaceRoot));
        var global = ReadConfigFile(GetGlobalConfigPath());
        var merged = TokenSaverBehaviorConfig.Merge(local, global);
        return merged?.RepoHelper ?? new RepoHelperBehaviorConfig();
    }

    /// <summary>Gets the local config file path for a workspace.</summary>
    public static string GetLocalConfigPath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ".tokensaver", "config.json");

    /// <summary>Gets the global config file path (machine-wide).</summary>
    public static string GetGlobalConfigPath()
    {
        var programData = Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData";
        return Path.Combine(programData, "TokenSaver", "config.json");
    }

    /// <summary>Updates a config value in the specified scope (local workspace or global).</summary>
    public static void UpdateConfigValue(
        string workspaceRoot,
        ConfigScope scope,
        Func<TokenSaverBehaviorConfig, TokenSaverBehaviorConfig> mutate)
    {
        var filePath = scope == ConfigScope.Local
            ? GetLocalConfigPath(workspaceRoot)
            : GetGlobalConfigPath();

        var current = ReadConfigFile(filePath) ?? new TokenSaverBehaviorConfig();
        var updated = mutate(current);
        WriteConfigFile(filePath, updated);
    }
}
