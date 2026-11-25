using AiDevTools.TokenSaver.CodeHelper.Models.Index;

using System.Text.RegularExpressions;

namespace AiDevTools.TokenSaver.CodeHelper.Indexing;

/// <summary>
///     Regex-based TypeScript/JavaScript symbol parser.
///     Handles class, interface, type alias, and function/arrow-function declarations.
/// </summary>
public sealed partial class TypeScriptSymbolParser : ISymbolParser
{
    public IReadOnlyList<string> SupportedExtensions => [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"];

    public IEnumerable<SymbolEntry> Parse(string filePath, string content)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        string? currentClass = null;
        int currentClassEnd = int.MaxValue;

        foreach (Match m in ClassRegex().Matches(content))
        {
            string name = m.Groups[1].Value;
            int line = GetLine(content, m.Index);
            currentClass = name;
            string? block = CodeParserUtils.ExtractBraceBlock(content, m.Index + m.Length - 1);
            currentClassEnd = block != null ? m.Index + m.Length + block.Length : int.MaxValue;

            yield return Make(filePath, name, name, SymbolKind.Class, line, null, now);
        }

        foreach (Match m in InterfaceRegex().Matches(content))
        {
            yield return Make(filePath, m.Groups[1].Value, m.Groups[1].Value, SymbolKind.Interface,
                GetLine(content, m.Index), null, now);
        }

        foreach (Match m in TypeAliasRegex().Matches(content))
        {
            yield return Make(filePath, m.Groups[1].Value, m.Groups[1].Value, SymbolKind.TypeAlias,
                GetLine(content, m.Index), null, now);
        }

        foreach (Match m in FunctionRegex().Matches(content))
        {
            string name = m.Groups[1].Value;
            if (name is "constructor" or "render" or "ngOnInit" or "ngOnDestroy")
            {
                continue;
            }

            yield return Make(filePath, name, name, SymbolKind.Function, GetLine(content, m.Index), null, now);
        }

        foreach (Match m in ArrowFunctionRegex().Matches(content))
        {
            string name = m.Groups[1].Value;
            yield return Make(filePath, name, name, SymbolKind.Function, GetLine(content, m.Index), null, now);
        }

        foreach (Match m in MethodRegex().Matches(content))
        {
            string name = m.Groups[1].Value;
            if (name is "constructor" or "render" or "super")
            {
                continue;
            }

            string? inType = m.Index < currentClassEnd ? currentClass : null;
            yield return Make(filePath, name, inType != null ? $"{inType}.{name}" : name,
                SymbolKind.Method, GetLine(content, m.Index), inType, now);
        }
    }

    [GeneratedRegex(
        @"^\s*(?:export\s+)?(?:abstract\s+)?class\s+(\w+)",
        RegexOptions.Multiline)]
    private static partial Regex ClassRegex();

    [GeneratedRegex(
        @"^\s*(?:export\s+)?interface\s+(\w+)",
        RegexOptions.Multiline)]
    private static partial Regex InterfaceRegex();

    [GeneratedRegex(
        @"^\s*(?:export\s+)?type\s+(\w+)\s*=",
        RegexOptions.Multiline)]
    private static partial Regex TypeAliasRegex();

    [GeneratedRegex(
        @"^\s{0,4}(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s+(\w+)\s*\(",
        RegexOptions.Multiline)]
    private static partial Regex FunctionRegex();

    [GeneratedRegex(
        @"^\s*(?:export\s+)?(?:const|let|var)\s+(\w+)\s*=\s*(?:async\s+)?\(?[^)]*\)?\s*=>",
        RegexOptions.Multiline)]
    private static partial Regex ArrowFunctionRegex();

    [GeneratedRegex(
        @"^\s{4,}(?:public\s+|private\s+|protected\s+|static\s+|async\s+)*(\w+)\s*\([^)]{0,300}\)\s*(?::\s*[\w<>\[\]|]+\s*)?\{",
        RegexOptions.Multiline)]
    private static partial Regex MethodRegex();

    private static SymbolEntry Make(string fp, string name, string fullName, SymbolKind kind, int line,
        string? containing, long now)
    {
        return new SymbolEntry
        {
            Id = CodeParserUtils.MakeId(fp, kind, name, line),
            Name = name,
            FullName = fullName,
            Kind = kind,
            FilePath = fp,
            Line = line,
            ContainingType = containing,
            IndexedAt = now
        };
    }

    private static int GetLine(string content, int index)
    {
        return content[..index].Count(c => c == '\n') + 1;
    }
}
