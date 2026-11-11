namespace TokenSaver.WslControl.Core;

using System.Text;
using System.Text.RegularExpressions;

using Community.Wsl.Sdk;

using TokenSaver.WslControl.Contracts;
using TokenSaver.WslControl.Models;

/// <summary>
///     Builds Windows command lines for <c>wsl.exe</c>. Each argument is quoted so spaces and
///     quotes reach the distro intact.
/// </summary>
public static class WslCommandLine
{
    public static string WslExe => Path.Combine(Environment.SystemDirectory, "wsl.exe");

    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return arg;
        }

        return "\"" + arg.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    public static string ForExec(string distro, string fileName, IEnumerable<string> args)
    {
        StringBuilder sb = new();
        _ = sb.Append(Quote(WslExe)).Append(" -d ").Append(Quote(distro)).Append(" -- ").Append(Quote(fileName));
        foreach (string a in args)
        {
            _ = sb.Append(' ').Append(Quote(a));
        }

        return sb.ToString();
    }
}

/// <summary>
///     <see cref="IWslApi" /> that asks <c>wsl.exe -l -v</c> in the console user's session. Used
///     when TokenSaver runs as LocalSystem, where the SDK's registry read sees no distros.
/// </summary>
public sealed partial class SessionWslApi(ISessionProcessRunner runner) : IWslApi
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(20);

    [GeneratedRegex(@"^\s*(\*)?\s*(\S+)\s+(\S+)\s+(\d+)\s*$")]
    private static partial Regex ListLine();

    public bool IsInstalled => File.Exists(WslCommandLine.WslExe);

    public bool IsWslSupported() => IsInstalled;

    public bool IsWslSupported(out string? missingCapabilities)
    {
        missingCapabilities = IsInstalled ? null : "wsl.exe was not found in the system directory.";
        return IsInstalled;
    }

    public IReadOnlyList<DistroInfo> GetDistributionList()
    {
        if (!IsInstalled)
        {
            return [];
        }

        SessionProcessResult result = runner.RunAsync(
            WslCommandLine.Quote(WslCommandLine.WslExe) + " -l -v", ListTimeout).GetAwaiter().GetResult();
        if (result.TimedOut || result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"wsl -l -v failed (exit {result.ExitCode}, timedOut={result.TimedOut}).");
        }

        return ParseDistroList(Encoding.Unicode.GetString(result.Stdout.Span));
    }

    public DistroInfo? GetDefaultDistribution() => GetDistributionList().FirstOrDefault(d => d.IsDefault);

    /// <summary>
    ///     Parses <c>wsl -l -v</c> output (UTF-16 text, header line, then NAME STATE VERSION rows).
    ///     A leading <c>*</c> marks the default distro.
    /// </summary>
    internal static IReadOnlyList<DistroInfo> ParseDistroList(string text)
    {
        List<DistroInfo> list = [];
        foreach (string raw in text.Replace("\0", string.Empty, StringComparison.Ordinal).Replace("﻿", string.Empty, StringComparison.Ordinal)
                     .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            Match m = ListLine().Match(raw);
            if (!m.Success)
            {
                continue;
            }

            list.Add(new DistroInfo
            {
                DistroName = m.Groups[2].Value,
                IsDefault = m.Groups[1].Success,
                WslVersion = int.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture),
                DefaultUid = 0,
            });
        }

        return list;
    }
}

/// <summary>
///     <see cref="IWslCommandExecutor" /> that runs commands in the console user's session through
///     <see cref="ISessionProcessRunner" />. Used when TokenSaver runs as LocalSystem.
/// </summary>
public sealed class SessionWslCommandExecutor(ISessionProcessRunner runner) : IWslCommandExecutor
{
    public async Task<WslCommandResult> ExecuteAsync(
        string distro,
        string fileName,
        string[] args,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        string display = fileName + " " + string.Join(' ', args);
        try
        {
            SessionProcessResult result = await runner.RunAsync(
                WslCommandLine.ForExec(distro, fileName, args), timeout);

            if (result.TimedOut)
            {
                return new WslCommandResult(
                    distro, display, ExitCode: -1, TimedOut: true,
                    ExecutionError: $"Command exceeded the {timeout.TotalSeconds:0}s timeout.");
            }

            return new WslCommandResult(
                distro,
                display,
                result.ExitCode,
                Stdout: Encoding.UTF8.GetString(result.Stdout.Span).TrimEnd('\n', '\r'),
                Stderr: Encoding.UTF8.GetString(result.Stderr.Span).TrimEnd('\n', '\r'));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new WslCommandResult(distro, display, ExitCode: -1, ExecutionError: ex.Message);
        }
    }
}
