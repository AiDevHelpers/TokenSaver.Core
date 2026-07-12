namespace TokenSaver.TokenEconomy.Models.Accuracy;

/// <summary>
///     A single accuracy test: a prompt derived from real workspace code,
///     with scoring criteria the model response must satisfy.
/// </summary>
public sealed class TestCase
{
    /// <summary>Short stable identifier (first 8 chars of a GUID).</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Agent type this test exercises: "code", "debug", "planning".</summary>
    public string AgentType { get; init; } = "code";

    /// <summary>
    ///     Test category:
    ///     completion — implement a stripped method body
    ///     review     — identify issues in a code snippet
    ///     docstring  — write XML/JSDoc documentation
    ///     bugfix     — fix a deliberate or real bug
    /// </summary>
    public string Category { get; init; } = "completion";

    /// <summary>The prompt sent to the model.</summary>
    public string Prompt { get; init; } = "";

    /// <summary>Workspace-relative path to the file this test was generated from.</summary>
    public string SourceFile { get; init; } = "";

    /// <summary>Programming language of the source (cs, ts, py, …).</summary>
    public string Language { get; init; } = "";

    /// <summary>
    ///     Regex patterns that MUST appear in the response to pass.
    ///     All patterns must match for the test to pass.
    /// </summary>
    public IReadOnlyList<string> ExpectedPatterns { get; init; } = [];

    /// <summary>
    ///     Regex patterns that must NOT appear in the response.
    ///     Any match is penalised.
    /// </summary>
    public IReadOnlyList<string> ForbiddenPatterns { get; init; } = [];

    /// <summary>Optional surrounding code included in the prompt for context.</summary>
    public string? Context { get; init; }
}
