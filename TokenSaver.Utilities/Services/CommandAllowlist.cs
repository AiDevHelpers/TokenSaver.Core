namespace TokenSaver.Utilities.Services;

/// <summary>Outcome of a <see cref="CommandAllowlist.Check" /> call.</summary>
public sealed record CommandCheck(bool Ok, string Command, string Arguments, string? Error)
{
    internal static CommandCheck Fail(string error) => new(false, "", "", error);
}

/// <summary>
///     Restricts a free-form command line (a test or lint command a client supplies) to a configured set of
///     executables. The executable must be a bare name from the list; paths, shells and anything unlisted are
///     refused. The remainder of the line is passed on as the argument string, never to a shell.
/// </summary>
public static class CommandAllowlist
{
    /// <summary>Default executables for test and lint commands (comma separated).</summary>
    public const string DefaultAllowed = "dotnet,npm,npx,pnpm,yarn,pytest,python,python3,go,cargo,mvn,gradle";

    private static readonly char[] s_pathChars = ['/', '\\', ':'];

    public static CommandCheck Check(string? commandLine, string? allowedCsv)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return CommandCheck.Fail("A command is required.");
        }

        string line = commandLine.Trim();
        int space = line.IndexOf(' ', StringComparison.Ordinal);
        string executable = space < 0 ? line : line[..space];
        string arguments = space < 0 ? "" : line[(space + 1)..];

        if (executable.IndexOfAny(s_pathChars) >= 0)
        {
            return CommandCheck.Fail($"'{executable}' is a path. Use a bare executable name from the allowed list.");
        }

        string name = executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? executable[..^4] : executable;
        string[] allowed = (string.IsNullOrWhiteSpace(allowedCsv) ? DefaultAllowed : allowedCsv)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return allowed.Contains(name, StringComparer.OrdinalIgnoreCase)
            ? new CommandCheck(true, executable, arguments, null)
            : CommandCheck.Fail(
                $"'{executable}' is not an allowed command. Allowed: {string.Join(", ", allowed)}. " +
                "Change 'AllowedCommands' in the TokenSaver configuration to add one.");
    }
}
