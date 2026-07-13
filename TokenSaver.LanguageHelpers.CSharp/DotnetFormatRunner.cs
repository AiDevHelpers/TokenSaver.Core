using TokenSaver.LanguageHelpers.Core.Models;
using TokenSaver.LanguageHelpers.Core.Process;

using Microsoft.Extensions.Logging;

using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenSaver.LanguageHelpers.CSharp;

/// <summary>Parsed outcome of one <c>dotnet format</c> invocation.</summary>
public sealed record DotnetFormatOutcome
{
    public required bool ToolAvailable { get; init; }
    public required bool WorkspaceFound { get; init; }
    public required int ExitCode { get; init; }
    public required string RawOutput { get; init; }
    public IReadOnlyList<CodeIssue> Issues { get; init; } = [];

    /// <summary>Absolute paths of files the run reported changes for (or would-change, in verify mode).</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
}

/// <summary>
///     Thin wrapper over the SDK's <c>dotnet format</c> command. Locates the workspace (nearest solution,
///     else nearest project) the scoped files belong to, invokes a format subcommand scoped with
///     <c>--include</c>, and parses the machine-readable <c>--report</c> JSON into
///     <see cref="CodeIssue" />s. Every C# fix/lint/format operation is expressed as one call here so the
///     helper stays declarative.
/// </summary>
public sealed class DotnetFormatRunner(IProcessRunner processRunner, ILogger logger)
{
    /// <summary>
    ///     Runs <c>dotnet format [subcommand]</c> over <paramref name="scope" />.
    /// </summary>
    /// <param name="subcommand">"whitespace", "style", "analyzers", or null for the full run.</param>
    /// <param name="verifyOnly">When true, adds <c>--verify-no-changes</c> so nothing is written (lint mode).</param>
    /// <param name="diagnostics">Optional analyzer/style diagnostic ids to restrict the run to.</param>
    /// <param name="severity">Optional minimum severity ("info", "warn", "error").</param>
    public async Task<DotnetFormatOutcome> RunAsync(
        string? subcommand,
        AnalysisScope scope,
        bool verifyOnly,
        IReadOnlyList<string>? diagnostics = null,
        string? severity = null,
        CancellationToken ct = default)
    {
        if (!await processRunner.IsAvailableAsync("dotnet", ct))
        {
            return new DotnetFormatOutcome
            {
                ToolAvailable = false,
                WorkspaceFound = false,
                ExitCode = -1,
                RawOutput = "The 'dotnet' CLI was not found on PATH."
            };
        }

        string? workspace = FindWorkspace(scope);
        if (workspace is null)
        {
            return new DotnetFormatOutcome
            {
                ToolAvailable = true,
                WorkspaceFound = false,
                ExitCode = -1,
                RawOutput = $"No .sln or .csproj found at or above '{scope.WorkspaceRoot}'. " +
                            "dotnet format requires a project or solution to operate on."
            };
        }

        string reportPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"tokensaver-format-{Guid.NewGuid():N}.json");

        List<string> args = ["format"];
        if (!string.IsNullOrEmpty(subcommand))
        {
            args.Add(subcommand);
        }

        args.Add(workspace);

        if (verifyOnly)
        {
            args.Add("--verify-no-changes");
        }

        if (severity is not null)
        {
            args.Add("--severity");
            args.Add(severity);
        }

        if (diagnostics is { Count: > 0 })
        {
            args.Add("--diagnostics");
            args.AddRange(diagnostics);
        }

        IReadOnlyList<string> includes = ResolveIncludes(scope, workspace);
        if (includes.Count > 0)
        {
            args.Add("--include");
            args.AddRange(includes);
        }

        args.Add("--report");
        args.Add(reportPath);

        ProcessResult result = await processRunner.RunAsync(
            "dotnet", args, System.IO.Path.GetDirectoryName(workspace)!, TimeSpan.FromMinutes(10), ct);

        (IReadOnlyList<CodeIssue> issues, IReadOnlyList<string> files) =
            await ParseReportAsync(reportPath, subcommand, ct);

        return new DotnetFormatOutcome
        {
            ToolAvailable = true,
            WorkspaceFound = true,
            ExitCode = result.ExitCode,
            RawOutput = result.CombinedOutput,
            Issues = issues,
            Files = files
        };
    }

    /// <summary>Walks up from the scope's targets/root to find the nearest solution, else the nearest project.</summary>
    private static string? FindWorkspace(AnalysisScope scope)
    {
        string start = scope.Targets.Count > 0
            ? (System.IO.Path.IsPathRooted(scope.Targets[0])
                ? scope.Targets[0]
                : System.IO.Path.Combine(scope.WorkspaceRoot, scope.Targets[0]))
            : scope.WorkspaceRoot;

        DirectoryInfo? dir = Directory.Exists(start)
            ? new DirectoryInfo(start)
            : new FileInfo(start).Directory;

        for (DirectoryInfo? d = dir; d is not null; d = d.Parent)
        {
            string? sln = Directory.EnumerateFiles(d.FullName, "*.sln").FirstOrDefault()
                          ?? Directory.EnumerateFiles(d.FullName, "*.slnx").FirstOrDefault();
            if (sln is not null)
            {
                return sln;
            }

            string? proj = Directory.EnumerateFiles(d.FullName, "*.csproj").FirstOrDefault();
            if (proj is not null)
            {
                return proj;
            }
        }

        return null;
    }

    /// <summary>Maps the scoped target files to paths relative to the workspace for <c>--include</c>.</summary>
    private static IReadOnlyList<string> ResolveIncludes(AnalysisScope scope, string workspace)
    {
        if (scope.Targets.Count == 0)
        {
            return [];
        }

        string workspaceDir = System.IO.Path.GetDirectoryName(workspace)!;
        List<string> includes = [];
        foreach (string target in scope.Targets)
        {
            string abs = System.IO.Path.IsPathRooted(target)
                ? target
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(scope.WorkspaceRoot, target));
            includes.Add(System.IO.Path.GetRelativePath(workspaceDir, abs));
        }

        return includes;
    }

    private async Task<(IReadOnlyList<CodeIssue>, IReadOnlyList<string>)> ParseReportAsync(
        string reportPath, string? subcommand, CancellationToken ct)
    {
        if (!File.Exists(reportPath))
        {
            return ([], []);
        }

        try
        {
            await using FileStream stream = File.OpenRead(reportPath);
            List<FormattedFileReport>? report =
                await JsonSerializer.DeserializeAsync<List<FormattedFileReport>>(stream, JsonOptions, ct);

            if (report is null)
            {
                return ([], []);
            }

            // Whitespace-only fixes are cosmetic; everything else (style/analyzers) is at least a warning.
            CodeIssueSeverity severity = subcommand == "whitespace"
                ? CodeIssueSeverity.Info
                : CodeIssueSeverity.Warning;

            List<CodeIssue> issues = [];
            List<string> files = [];
            foreach (FormattedFileReport file in report)
            {
                if (string.IsNullOrEmpty(file.FilePath))
                {
                    continue;
                }

                files.Add(file.FilePath);
                foreach (FileChangeReport change in file.FileChanges ?? [])
                {
                    issues.Add(new CodeIssue
                    {
                        Severity = severity,
                        Message = change.FormatDescription ?? "Formatting change.",
                        RuleId = change.DiagnosticId,
                        IsFixable = true,
                        Location = new CodeLocation
                        {
                            FilePath = file.FilePath,
                            Line = change.LineNumber,
                            Column = change.CharNumber
                        }
                    });
                }
            }

            return (issues, files);
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Failed to parse dotnet format report at {Path}", reportPath);
            return ([], []);
        }
        finally
        {
            TryDelete(reportPath);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not delete temp report {Path}", path);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record FormattedFileReport
    {
        [JsonPropertyName("FilePath")]
        public string? FilePath { get; init; }

        [JsonPropertyName("FileChanges")]
        public List<FileChangeReport>? FileChanges { get; init; }
    }

    private sealed record FileChangeReport
    {
        [JsonPropertyName("LineNumber")]
        public int LineNumber { get; init; }

        [JsonPropertyName("CharNumber")]
        public int CharNumber { get; init; }

        [JsonPropertyName("DiagnosticId")]
        public string? DiagnosticId { get; init; }

        [JsonPropertyName("FormatDescription")]
        public string? FormatDescription { get; init; }
    }
}
