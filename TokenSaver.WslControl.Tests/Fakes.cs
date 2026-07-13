namespace TokenSaver.WslControl.Tests;

using Community.Wsl.Sdk;

using TokenSaver.WslControl.Contracts;
using TokenSaver.WslControl.Models;

/// <summary>In-memory <see cref="IWslApi" /> so provider logic runs without a real WSL install.</summary>
internal sealed class FakeWslApi : IWslApi
{
    public bool Supported { get; init; } = true;
    public string? MissingReason { get; init; }
    public List<DistroInfo> Distros { get; init; } = [];

    public bool IsInstalled => Supported;

    public bool IsWslSupported() => Supported;

    public bool IsWslSupported(out string? missingCapabilities)
    {
        missingCapabilities = Supported ? null : MissingReason ?? "not supported";
        return Supported;
    }

    public DistroInfo? GetDefaultDistribution()
    {
        foreach (DistroInfo d in Distros)
        {
            if (d.IsDefault)
            {
                return d;
            }
        }

        return null;
    }

    public IReadOnlyList<DistroInfo> GetDistributionList() => Distros;

    public static DistroInfo Distro(string name, bool isDefault = false, int version = 2) =>
        new() { DistroName = name, IsDefault = isDefault, WslVersion = version, DefaultUid = 1000 };
}

/// <summary>
///     Captures every executed command and returns a scripted result. Lets tests assert on the
///     exact command line the provider built without running anything.
/// </summary>
internal sealed class FakeExecutor : IWslCommandExecutor
{
    private readonly Func<string, WslCommandResult>? _fromCommandLine;

    public FakeExecutor(Func<string, WslCommandResult>? fromCommandLine = null) =>
        _fromCommandLine = fromCommandLine;

    public List<(string Distro, string FileName, string[] Args)> Calls { get; } = [];

    public string? LastCommandLine => Calls.Count > 0 && Calls[^1].Args.Length >= 2 ? Calls[^1].Args[1] : null;

    public Task<WslCommandResult> ExecuteAsync(
        string distro, string fileName, string[] args, TimeSpan timeout, CancellationToken ct = default)
    {
        Calls.Add((distro, fileName, args));
        string line = args.Length >= 2 ? args[1] : string.Join(' ', args);
        WslCommandResult result = _fromCommandLine?.Invoke(line)
            ?? new WslCommandResult(distro, line, 0, Stdout: "ok");
        return Task.FromResult(result);
    }
}

/// <summary>Returns a canned model reply (or throws) for planner tests.</summary>
internal sealed class FakeCommandModel : IWslCommandModel
{
    private readonly string? _reply;
    private readonly Exception? _throw;

    public FakeCommandModel(string reply) => _reply = reply;

    public FakeCommandModel(Exception toThrow) => _throw = toThrow;

    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default) =>
        _throw is not null ? Task.FromException<string>(_throw) : Task.FromResult(_reply!);
}
