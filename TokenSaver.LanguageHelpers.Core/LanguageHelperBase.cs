using TokenSaver.LanguageHelpers.Core.Contracts;
using TokenSaver.LanguageHelpers.Core.Models;
using TokenSaver.LanguageHelpers.Core.Process;

using Microsoft.Extensions.Logging;

using System.Diagnostics;

namespace TokenSaver.LanguageHelpers.Core;

/// <summary>
///     Shared plumbing for language helpers: default <see cref="OperationStatus.NotApplicable" />
///     implementations (so a helper only overrides what its language actually supports), file
///     enumeration honoring the scope's extensions and recursion flag, and a stopwatch-timed wrapper that
///     turns tool availability + exceptions into consistent results. Concrete helpers override the
///     operations they support and use the <c>Timed*</c>/<c>*Result</c> helpers to shape their output.
/// </summary>
public abstract class LanguageHelperBase(IProcessRunner processRunner, ILogger logger) : ILanguageHelper
{
    protected IProcessRunner ProcessRunner { get; } = processRunner;
    protected ILogger Logger { get; } = logger;

    /// <inheritdoc />
    public abstract LanguageId Language { get; }

    /// <inheritdoc />
    public abstract IReadOnlyCollection<string> FileExtensions { get; }

    /// <inheritdoc />
    public bool CanHandle(string filePath)
    {
        string ext = System.IO.Path.GetExtension(filePath);
        return FileExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    // ----- Default implementations: a language that doesn't support an operation inherits NotApplicable. -----

    /// <inheritdoc />
    public virtual Task<EnableAnalysisResult> EnableCodeAnalysisAsync(
        AnalysisScope scope, CodeStandard? standard = null, CancellationToken ct = default) =>
        Task.FromResult(NotApplicable<EnableAnalysisResult>("EnableCodeAnalysis"));

    /// <inheritdoc />
    public virtual Task<SyntaxCheckReport> RunSyntaxCheckerAsync(AnalysisScope scope, CancellationToken ct = default) =>
        Task.FromResult(NotApplicable<SyntaxCheckReport>("RunSyntaxChecker"));

    /// <inheritdoc />
    public virtual Task<FixResult> FixSyntaxAsync(AnalysisScope scope, CancellationToken ct = default) =>
        Task.FromResult(NotApplicable<FixResult>("FixSyntax"));

    /// <inheritdoc />
    public virtual Task<ImportFixResult> FixImportsAsync(AnalysisScope scope, CancellationToken ct = default) =>
        Task.FromResult(NotApplicable<ImportFixResult>("FixImports"));

    /// <inheritdoc />
    public virtual Task<NamespaceFixResult> FixNamespaceAsync(AnalysisScope scope, CancellationToken ct = default) =>
        Task.FromResult(NotApplicable<NamespaceFixResult>("FixNamespace"));

    /// <inheritdoc />
    public virtual Task<LintReport> RunLintAsync(AnalysisScope scope, LintOptions? options = null, CancellationToken ct = default) =>
        Task.FromResult(NotApplicable<LintReport>("RunLint"));

    /// <inheritdoc />
    public virtual Task<FormatResult> FormatCodeAsync(AnalysisScope scope, FormatOptions? options = null, CancellationToken ct = default) =>
        Task.FromResult(NotApplicable<FormatResult>("FormatCode"));

    // ----- Helpers for concrete implementations. -----

    /// <summary>
    ///     Builds a <see cref="OperationStatus.NotApplicable" /> result of type <typeparamref name="T" /> with
    ///     an explanatory message. Used both by the defaults above and by helpers declining a specific op.
    /// </summary>
    protected T NotApplicable<T>(string operation) where T : OperationResult, new() => new()
    {
        Status = OperationStatus.NotApplicable,
        Messages = [$"'{operation}' is not applicable to {Language}."]
    };

    /// <summary>Builds a <see cref="OperationStatus.ToolUnavailable" /> result for a missing external tool.</summary>
    protected T ToolUnavailable<T>(string tool) where T : OperationResult, new() => new()
    {
        Status = OperationStatus.ToolUnavailable,
        Tool = tool,
        Messages = [$"Required tool '{tool}' was not found on PATH. Install it to enable this operation."]
    };

    /// <summary>
    ///     Enumerates the concrete source files this helper owns within <paramref name="scope" />, expanding
    ///     directory targets (honoring <see cref="AnalysisScope.Recursive" />) and filtering by
    ///     <see cref="FileExtensions" />. An empty <see cref="AnalysisScope.Targets" /> means the whole root.
    /// </summary>
    protected IReadOnlyList<string> EnumerateFiles(AnalysisScope scope)
    {
        IReadOnlyList<string> targets = scope.Targets.Count == 0 ? [scope.WorkspaceRoot] : scope.Targets;
        SearchOption search = scope.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        List<string> files = [];

        foreach (string target in targets)
        {
            string resolved = System.IO.Path.IsPathRooted(target)
                ? target
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(scope.WorkspaceRoot, target));

            if (Directory.Exists(resolved))
            {
                foreach (string ext in FileExtensions)
                {
                    files.AddRange(Directory.EnumerateFiles(resolved, $"*{ext}", search));
                }
            }
            else if (File.Exists(resolved) && CanHandle(resolved))
            {
                files.Add(resolved);
            }
        }

        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    ///     Runs <paramref name="body" /> under a stopwatch, stamping <see cref="OperationResult.Duration" />
    ///     onto its result and converting an unexpected exception into a <see cref="OperationStatus.Failed" />
    ///     result of type <typeparamref name="T" />. Cancellation is allowed to propagate.
    /// </summary>
    protected async Task<T> TimedAsync<T>(Func<Task<T>> body, CancellationToken ct)
        where T : OperationResult, new()
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            T result = await body();
            result.Duration = sw.Elapsed;
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Language} operation failed", Language);
            return new T
            {
                Status = OperationStatus.Failed,
                Duration = sw.Elapsed,
                Messages = [$"Operation failed: {ex.Message}"],
                RawOutput = ex.ToString()
            };
        }
    }
}
