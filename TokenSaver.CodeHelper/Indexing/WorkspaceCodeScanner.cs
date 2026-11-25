using AiDevTools.TokenSaver.TokenEconomy.Models.Accuracy;
using AiDevTools.TokenSaver.Utilities.Services;

using Microsoft.Extensions.Logging;

using System.Text.RegularExpressions;

namespace AiDevTools.TokenSaver.CodeHelper.Indexing;

/// <summary>
///     Scans workspace source files and generates <see cref="TestCase" /> objects
///     that exercise the local model on real code from the project.
///     Supported languages: C# (.cs), TypeScript (.ts)
///     Test categories generated: completion, review, docstring
/// </summary>
public sealed partial class WorkspaceCodeScanner(ILogger<WorkspaceCodeScanner> logger)
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipped {File}: {Msg}")]
    private static partial void LogFileSkipped(ILogger logger, string file, string msg);

    private static readonly string[] SkipDirs =
        ["bin", "obj", "node_modules", ".git", ".tokensaver", "dist", "out", ".vscode"];

    private static readonly string[] CodeExtensions = [".CS", ".TS"];

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<List<TestCase>> ScanAsync(string workspaceRoot, int maxCases = 30, CancellationToken ct = default)
    {
        List<TestCase> all = [];
        GitignoreFilter gitignore = GitignoreFilter.Load(workspaceRoot);

        foreach (string file in EnumerateCodeFiles(workspaceRoot, gitignore))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                string text = await File.ReadAllTextAsync(file, ct);
                string rel = Path.GetRelativePath(workspaceRoot, file).Replace('\\', '/');
                string ext = Path.GetExtension(file).ToUpperInvariant();

                List<TestCase> cases = ext switch
                {
                    ".CS" => ExtractCSharpCases(text, rel),
                    ".TS" => ExtractTypeScriptCases(text, rel),
                    _ => []
                };

                all.AddRange(cases);
            }
            catch (Exception ex)
            {
                LogFileSkipped(logger, file, ex.Message);
            }
        }

        // Non-cryptographic shuffle used only to pick a varied sample of test cases;
        // no security-sensitive randomness is required here.
#pragma warning disable CA5394
        Random rng = new();
        all.Sort((_, _) => rng.Next(-1, 2));
#pragma warning restore CA5394
        return [.. all.Take(maxCases)];
    }

    // ── File enumeration ──────────────────────────────────────────────────────

    private static IEnumerable<string> EnumerateCodeFiles(string root, GitignoreFilter gitignore)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        Queue<string> queue = new();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            string dir = queue.Dequeue();
            string dirName = Path.GetFileName(dir);

            if (SkipDirs.Contains(dirName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir))
            {
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (gitignore.IsIgnored(rel, false))
                {
                    continue;
                }

                if (CodeExtensions.Contains(Path.GetExtension(file).ToUpperInvariant()))
                {
                    yield return file;
                }
            }

            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                string rel = Path.GetRelativePath(root, sub).Replace('\\', '/');
                if (!gitignore.IsIgnored(rel, true))
                {
                    queue.Enqueue(sub);
                }
            }
        }
    }

    // ── C# extraction ─────────────────────────────────────────────────────────

    // Reuses the same method regex as CSharpSymbolParser
    [GeneratedRegex(
        @"^\s{4,}((?:(?:public|private|protected|internal|static|async|override|virtual|abstract|sealed|new)\s+)+)" +
        @"(Task(?:<[^>]+>)?|ValueTask(?:<[^>]+>)?|(?:string|int|bool|void|double|float|long|object|[A-Z]\w*)(?:<[^>]+>)?(?:\[\])?)\s+" +
        @"(\w+)\s*(\([^)]*\))",
        RegexOptions.Multiline)]
    private static partial Regex CSharpMethodRegex();

    private static List<TestCase> ExtractCSharpCases(string source, string relPath)
    {
        List<TestCase> cases = [];

        foreach (Match m in CSharpMethodRegex().Matches(source))
        {
            string returnType = m.Groups[2].Value.Trim();
            string methodName = m.Groups[3].Value;
            string parameters = m.Groups[4].Value;

            if (methodName is "Main" or "Dispose" or "ToString" or "GetHashCode" or "Equals")
            {
                continue;
            }

            string? body = CodeParserUtils.ExtractBraceBlock(source, m.Index + m.Length);
            if (body is null || body.Length < 20 || body.Length > 2000)
            {
                continue;
            }

            string signature = $"{m.Groups[1].Value.Trim()} {returnType} {methodName}{parameters}";
            cases.Add(MakeCompletionCase(signature, body, relPath, "cs"));
            cases.Add(MakeReviewCase(signature, body, relPath, "cs"));

            if (returnType != "void" && !returnType.StartsWith("Task", StringComparison.Ordinal) && !returnType.StartsWith("ValueTask", StringComparison.Ordinal))
            {
                cases.Add(MakeDocstringCase(signature, relPath, "cs"));
            }
        }

        return cases;
    }

    // ── TypeScript extraction ─────────────────────────────────────────────────

    [GeneratedRegex(
        @"^\s{0,4}(?:export\s+)?(?:public\s+)?(?:private\s+)?(?:async\s+)?(?:function\s+)?(\w+)\s*(\([^)]{0,200}\))\s*(?::\s*[\w<>\[\]|]+)?\s*\{",
        RegexOptions.Multiline)]
    private static partial Regex TypeScriptFunctionRegex();

    private static List<TestCase> ExtractTypeScriptCases(string source, string relPath)
    {
        List<TestCase> cases = [];

        foreach (Match m in TypeScriptFunctionRegex().Matches(source))
        {
            string funcName = m.Groups[1].Value;
            if (funcName is "constructor" or "ngOnInit" or "ngOnDestroy" or "render")
            {
                continue;
            }

            string? body = CodeParserUtils.ExtractBraceBlock(source, m.Index + m.Length - 1);
            if (body is null || body.Length < 15 || body.Length > 1500)
            {
                continue;
            }

            string signature = m.Value.TrimEnd('{').Trim();
            cases.Add(MakeCompletionCase(signature, body, relPath, "ts"));
            cases.Add(MakeReviewCase(signature, body, relPath, "ts"));
        }

        return cases;
    }

    // ── Test case factories ───────────────────────────────────────────────────

    private static TestCase MakeCompletionCase(string signature, string body, string relPath, string lang)
    {
        string stripped = lang == "cs"
            ? $"{signature}\n{{\n    // TODO: implement\n    throw new NotImplementedException();\n}}"
            : $"{signature} {{\n    // TODO: implement\n    throw new Error('not implemented');\n}}";

        return new TestCase
        {
            AgentType = "code",
            Category = "completion",
            Language = lang,
            SourceFile = relPath,
            Prompt = $"Implement this {(lang == "cs" ? "C#" : "TypeScript")} method.\n" +
                     $"Output only the completed method body — no explanation.\n\n" +
                     $"```{lang}\n{stripped}\n```",
            ExpectedPatterns = lang == "cs"
                ? [$@"\b{Regex.Escape(signature.Split(' ').Last().Split('(')[0])}\b", @"\{", @"\}"]
                : [$@"\b{Regex.Escape(signature.Split('(')[0].Trim().Split(' ').Last())}\b", @"\{", @"\}"],
            ForbiddenPatterns =
            [
                @"I cannot",
                @"I don't know",
                @"```\s*```"
            ],
            Context = body
        };
    }

    private static TestCase MakeReviewCase(string signature, string body, string relPath, string lang)
    {
        string snippet = $"```{lang}\n{signature}\n{{\n{body}\n}}\n```";

        return new TestCase
        {
            AgentType = "code",
            Category = "review",
            Language = lang,
            SourceFile = relPath,
            Prompt = $"Review this {(lang == "cs" ? "C#" : "TypeScript")} code.\n" +
                     $"List specific issues with null safety, error handling, performance, or correctness.\n" +
                     $"If there are no issues, say 'No issues found.'\n\n{snippet}",
            ExpectedPatterns = [@"(?i)(issue|problem|bug|error|warning|null|exception|No issues)", @"\w{10,}"],
            ForbiddenPatterns = [@"I cannot", @"I don't understand"]
        };
    }

    private static TestCase MakeDocstringCase(string signature, string relPath, string lang)
    {
        return new TestCase
        {
            AgentType = "code",
            Category = "docstring",
            Language = lang,
            SourceFile = relPath,
            Prompt = lang == "cs"
                ? $"Write an XML documentation comment for this C# method.\n" +
                  $"Output only the comment — no code.\n\n```cs\n{signature}\n```"
                : $"Write a JSDoc comment for this TypeScript function.\n" +
                  $"Output only the comment — no code.\n\n```ts\n{signature}\n```",
            ExpectedPatterns = lang == "cs"
                ? [@"<summary>", @"</summary>"]
                : [@"@param|@returns|\/\*\*"],
            ForbiddenPatterns = [@"I cannot", @"```\s*```"]
        };
    }
}
