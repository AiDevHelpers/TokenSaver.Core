namespace AiDevTools.TokenSaver.WslControl.Core;

using System.Text.RegularExpressions;

using Community.Wsl.Sdk;

using Microsoft.Extensions.Logging;

using AiDevTools.TokenSaver.WslControl.Contracts;
using AiDevTools.TokenSaver.WslControl.Models;

/// <summary>
///     Default <see cref="IWslProvider" />. Detection and enumeration go through the SDK's
///     <see cref="IWslApi" />; all command execution goes through <see cref="IWslCommandExecutor" />
///     (the seam that tests replace). Shell lines are executed via <c>/bin/bash -c</c> so pipes,
///     redirection, and env work; package/argument values are single-quoted to avoid injection.
/// </summary>
public sealed partial class WslProvider(
    IWslApi wslApi,
    IWslCommandExecutor executor,
    ILogger<WslProvider> logger) : IWslProvider
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "WSL not supported: {Reason}")]
    private static partial void LogNotSupported(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to enumerate WSL distributions")]
    private static partial void LogEnumerationFailed(ILogger logger, Exception ex);

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan AptTimeout = TimeSpan.FromMinutes(15);

    // apt/dpkg package names: letters, digits, and + - . _ (covers e.g. g++, lib32z1, python3.11).
    private static readonly Regex ValidPackageName = MyPackageRegex();

    private readonly IWslApi _wslApi = wslApi;
    private readonly IWslCommandExecutor _executor = executor;
    private readonly ILogger<WslProvider> _logger = logger;

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9+._-]*$", RegexOptions.Compiled)]
    private static partial Regex MyPackageRegex();

    // ── Detection & enumeration ─────────────────────────────────────────────────

    public Task<WslAvailability> CheckAvailabilityAsync(CancellationToken ct = default)
    {
        if (!_wslApi.IsWslSupported(out string? reason))
        {
            LogNotSupported(_logger, reason ?? "unknown");
            return Task.FromResult(new WslAvailability(
                IsInstalled: false,
                Reason: string.IsNullOrWhiteSpace(reason) ? "WSL is not installed or not supported." : reason,
                InstallUrl: WslAvailability.DefaultInstallUrl));
        }

        try
        {
            List<DistroInfo> distros = _wslApi.GetDistributionList().ToList();
            return Task.FromResult(new WslAvailability(
                IsInstalled: true,
                DefaultDistro: distros.Where(d => d.IsDefault).Select(d => d.DistroName).FirstOrDefault(),
                DistroCount: distros.Count,
                InstallUrl: WslAvailability.DefaultInstallUrl));
        }
        catch (Exception ex)
        {
            LogEnumerationFailed(_logger, ex);
            return Task.FromResult(new WslAvailability(
                IsInstalled: true,
                Reason: $"WSL is present but enumerating distributions failed: {ex.Message}",
                InstallUrl: WslAvailability.DefaultInstallUrl));
        }
    }

    public Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken ct = default)
    {
        try
        {
            IReadOnlyList<WslDistroInfo> mapped = _wslApi.GetDistributionList()
                .Select(Map)
                .ToList();
            return Task.FromResult(mapped);
        }
        catch (Exception ex)
        {
            LogEnumerationFailed(_logger, ex);
            return Task.FromResult<IReadOnlyList<WslDistroInfo>>([]);
        }
    }

    public Task<WslDistroInfo?> GetDefaultDistroAsync(CancellationToken ct = default)
    {
        try
        {
            DistroInfo? def = _wslApi.GetDefaultDistribution();
            return Task.FromResult(def is { } d ? Map(d) : null);
        }
        catch (Exception ex)
        {
            LogEnumerationFailed(_logger, ex);
            return Task.FromResult<WslDistroInfo?>(null);
        }
    }

    // ── Health ──────────────────────────────────────────────────────────────────

    public async Task<WslHealthReport> CheckHealthAsync(string? distro = null, CancellationToken ct = default)
    {
        (string? resolved, string? error) = ResolveDistro(distro);
        if (resolved is null)
        {
            return new WslHealthReport(distro ?? "(default)", IsHealthy: false, Detail: error);
        }

        // One probe collects everything as KEY=VALUE lines so parsing is order-independent.
        const string probe =
            "printf 'KERNEL=%s\\n' \"$(uname -sr)\"; " +
            "printf 'USER=%s\\n' \"$(id -un)\"; " +
            "( . /etc/os-release 2>/dev/null; printf 'OS=%s\\n' \"$PRETTY_NAME\" ); " +
            "if command -v apt-get >/dev/null 2>&1; then printf 'APT=1\\n'; else printf 'APT=0\\n'; fi; " +
            "if sudo -n true >/dev/null 2>&1; then printf 'SUDO=1\\n'; else printf 'SUDO=0\\n'; fi";

        WslCommandResult result = await _executor.ExecuteAsync(
            resolved, "/bin/bash", ["-c", probe], DefaultTimeout, ct);

        if (!result.Succeeded)
        {
            string detail = result.ExecutionError
                ?? (string.IsNullOrWhiteSpace(result.Stderr) ? $"probe exited {result.ExitCode}" : result.Stderr);
            return new WslHealthReport(resolved, IsHealthy: false, Detail: detail);
        }

        Dictionary<string, string> fields = ParseKeyValues(result.Stdout);
        string? kernel = fields.GetValueOrDefault("KERNEL");
        bool healthy = !string.IsNullOrWhiteSpace(kernel);

        return new WslHealthReport(
            Distro: resolved,
            IsHealthy: healthy,
            OsDescription: fields.GetValueOrDefault("OS"),
            Kernel: kernel,
            DefaultUser: fields.GetValueOrDefault("USER"),
            HasSudo: fields.GetValueOrDefault("SUDO") == "1",
            HasApt: fields.GetValueOrDefault("APT") == "1",
            Detail: healthy ? null : "Probe ran but returned no kernel information.");
    }

    // ── Command execution ───────────────────────────────────────────────────────

    public Task<WslCommandResult> RunBashAsync(
        string commandLine,
        string? distro = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        (string? resolved, string? error) = ResolveDistro(distro);
        return resolved is null
            ? Task.FromResult(new WslCommandResult(distro ?? "(default)", commandLine, -1, ExecutionError: error))
            : _executor.ExecuteAsync(resolved, "/bin/bash", ["-c", commandLine], timeout ?? DefaultTimeout, ct);
    }

    public Task<WslCommandResult> AptUpdateAsync(string? distro = null, CancellationToken ct = default) =>
        RunBashAsync("sudo -n env DEBIAN_FRONTEND=noninteractive apt-get update", distro, AptTimeout, ct);

    public Task<WslCommandResult> AptUpgradeAsync(string? distro = null, CancellationToken ct = default) =>
        RunBashAsync("sudo -n env DEBIAN_FRONTEND=noninteractive apt-get upgrade -y", distro, AptTimeout, ct);

    public Task<WslCommandResult> AptInstallAsync(
        IReadOnlyList<string> packages, string? distro = null, CancellationToken ct = default)
    {
        List<string> invalid = packages.Where(p => !ValidPackageName.IsMatch(p)).ToList();
        if (packages.Count == 0 || invalid.Count > 0)
        {
            string msg = packages.Count == 0
                ? "No packages specified."
                : $"Rejected invalid package name(s): {string.Join(", ", invalid)}";
            return Task.FromResult(new WslCommandResult(distro ?? "(default)", "apt-get install", -1, ExecutionError: msg));
        }

        string pkgList = string.Join(' ', packages.Select(BashQuote));
        return RunBashAsync(
            $"sudo -n env DEBIAN_FRONTEND=noninteractive apt-get install -y {pkgList}", distro, AptTimeout, ct);
    }

    public Task<WslCommandResult> BuildAsync(
        string workingDirectory,
        string buildCommand,
        string? distro = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        bool looksWindows = workingDirectory.Contains(':', StringComparison.Ordinal)
                            || workingDirectory.Contains('\\', StringComparison.Ordinal);

        // Windows paths are translated at runtime via wslpath; native Linux paths are used as-is.
        string cd = looksWindows
            ? $"cd \"$(wslpath -a {BashQuote(workingDirectory)})\""
            : $"cd {BashQuote(workingDirectory)}";

        return RunBashAsync($"{cd} && {buildCommand}", distro, timeout ?? TimeSpan.FromMinutes(10), ct);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private (string? Distro, string? Error) ResolveDistro(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return (requested, null);
        }

        try
        {
            DistroInfo? def = _wslApi.GetDefaultDistribution();
            return def is { } d
                ? (d.DistroName, null)
                : (null, "No WSL distribution is registered. Install one, e.g. `wsl --install -d Ubuntu`.");
        }
        catch (Exception ex)
        {
            return (null, $"Could not resolve the default WSL distribution: {ex.Message}");
        }
    }

    private static WslDistroInfo Map(DistroInfo d) =>
        new(d.DistroName, d.IsDefault, d.WslVersion, d.BasePath, d.DefaultUid);

    private static Dictionary<string, string> ParseKeyValues(string text)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        return map;
    }

    /// <summary>Single-quotes a value for safe use in a bash command line.</summary>
    internal static string BashQuote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
