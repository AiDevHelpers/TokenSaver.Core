using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Reflection;

namespace TokenSaver.Docs;

/// <summary>
///     Exposes the TokenSaver documentation files as MCP resources.
///     Docs are embedded in the assembly at build time so no extra file deployment is needed.
///     Resources:
///     docs://tokensaver/spec          — full implementation spec
///     docs://tokensaver/agent         — agent trigger guidance
///     docs://tokensaver/test-plan     — question-handling test plan
///     docs://tokensaver/accuracy-test — Ollama chain accuracy test
///     docs://tokensaver/context-test  — context cache / hygiene test
/// </summary>
// Not `static` — ModelContextProtocol.Server's WithResources<T>() requires a non-static type
// argument (CS0718 otherwise). The individual resource methods remain static since they're
// stateless; the SDK only needs the type itself to be a valid generic argument.
[McpServerResourceType]
public sealed class DocResourceService
{
    private DocResourceService()
    {
    }

    private static string Load(string fileName)
    {
        Assembly asm = typeof(DocResourceService).Assembly;
        string name = $"TokenSaver.Docs.Resources.{fileName}";
        using Stream stream = asm.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException(
                               $"Embedded resource '{name}' not found. " +
                               $"Available: {string.Join(", ", asm.GetManifestResourceNames())}");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    [McpServerResource(UriTemplate = "docs://tokensaver/spec", Name = "TokenSaver Spec", MimeType = "text/markdown")]
    [Description(
        "Full implementation spec for the TokenSaver MCP server: tools, execution flow, context management, question handling, and model settings.")]
    public static string GetSpec()
    {
        return Load("SPEC.md");
    }

    [McpServerResource(UriTemplate = "docs://tokensaver/agent", Name = "TokenSaver Agent Guide",
        MimeType = "text/markdown")]
    [Description(
        "Agent trigger guidance: when and how to invoke the TokenSaver MCP server and which tool to use for concise context generation.")]
    public static string GetAgent()
    {
        return Load("agent.md");
    }

    [McpServerResource(UriTemplate = "docs://tokensaver/test-plan", Name = "TokenSaver Test Plan",
        MimeType = "text/markdown")]
    [Description("Test plan for verifying the question-handling flow: autonomous resolution vs. user escalation.")]
    public static string GetTestPlan()
    {
        return Load("test_plan.md");
    }

    [McpServerResource(UriTemplate = "docs://tokensaver/accuracy-test", Name = "TokenSaver Accuracy Test",
        MimeType = "text/markdown")]
    [Description("Accuracy test for the DraftModel chain on a real Python helper task.")]
    public static string GetAccuracyTest()
    {
        return Load("accuracy_test.md");
    }

    [McpServerResource(UriTemplate = "docs://tokensaver/context-test", Name = "TokenSaver Context Test",
        MimeType = "text/markdown")]
    [Description(
        "Context cache and hygiene workflow test: validates that .tokensaver artifacts are only used on explicit trigger.")]
    public static string GetContextTest()
    {
        return Load("context_test.md");
    }
}
