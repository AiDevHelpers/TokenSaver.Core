using TokenSaver.CodeHelper.Models.Index;

using System.Text.RegularExpressions;

namespace TokenSaver.CodeHelper.Indexing;

/// <summary>
///     Fallback symbol parser for Python, Go, Java, and other languages.
///     Uses simple, broadly-applicable patterns.
/// </summary>
public sealed partial class GenericSymbolParser : ISymbolParser
{
    public IReadOnlyList<string> SupportedExtensions => [".py", ".go", ".java", ".rb", ".php", ".rs", ".swift", ".kt"];

    public IEnumerable<SymbolEntry> Parse(string filePath, string content)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string ext = Path.GetExtension(filePath).ToUpperInvariant();

        return ext switch
        {
            ".PY" => ParseWithPatterns(filePath, content, now, [PythonClassRegex(), PythonFuncRegex()],
                [SymbolKind.Class, SymbolKind.Function]),
            ".GO" => ParseWithPatterns(filePath, content, now, [GoFuncRegex()],
                [SymbolKind.Function]),
            ".JAVA" => ParseWithPatterns(filePath, content, now, [JavaTypeRegex(), JavaMethodRegex()],
                [SymbolKind.Class, SymbolKind.Method]),
            ".RS" => ParseWithPatterns(filePath, content, now, [RustTypeRegex(), RustFnRegex()],
                [SymbolKind.Class, SymbolKind.Function]),
            _ => ParseWithPatterns(filePath, content, now, [PythonFuncRegex(), PythonClassRegex()],
                [SymbolKind.Function, SymbolKind.Class])
        };
    }

    // Python: def foo(...) or class Foo
    [GeneratedRegex(@"^(?:async\s+)?def\s+(\w+)\s*\(", RegexOptions.Multiline)]
    private static partial Regex PythonFuncRegex();

    [GeneratedRegex(@"^class\s+(\w+)", RegexOptions.Multiline)]
    private static partial Regex PythonClassRegex();

    // Go: func (recv) FuncName( or func FuncName(
    [GeneratedRegex(@"^func\s+(?:\([^)]+\)\s+)?(\w+)\s*\(", RegexOptions.Multiline)]
    private static partial Regex GoFuncRegex();

    // Java: public/private class/interface/method
    [GeneratedRegex(
        @"^\s*(?:public|private|protected|static|final|abstract|synchronized|\s)*(?:class|interface|enum)\s+(\w+)",
        RegexOptions.Multiline)]
    private static partial Regex JavaTypeRegex();

    [GeneratedRegex(
        @"^\s{4,}(?:public|private|protected|static|final|synchronized|\s)+(?:[\w<>\[\]]+)\s+(\w+)\s*\(",
        RegexOptions.Multiline)]
    private static partial Regex JavaMethodRegex();

    // Rust: fn name(
    [GeneratedRegex(@"^\s*(?:pub(?:\([^)]+\))?\s+)?(?:async\s+)?fn\s+(\w+)\s*[<(]", RegexOptions.Multiline)]
    private static partial Regex RustFnRegex();

    // Generic struct/enum
    [GeneratedRegex(@"^\s*(?:pub(?:\([^)]+\))?\s+)?(?:struct|enum|trait|impl)\s+(\w+)", RegexOptions.Multiline)]
    private static partial Regex RustTypeRegex();

    private static IEnumerable<SymbolEntry> ParseWithPatterns(
        string filePath, string content, long now,
        Regex[] regexes, SymbolKind[] kinds)
    {
        for (int i = 0; i < regexes.Length; i++)
        {
            SymbolKind kind = kinds[Math.Min(i, kinds.Length - 1)];
            foreach (Match m in regexes[i].Matches(content))
            {
                string name = m.Groups[1].Value;
                int line = content[..m.Index].Count(c => c == '\n') + 1;
                yield return new SymbolEntry
                {
                    Id = CodeParserUtils.MakeId(filePath, kind, name, line),
                    Name = name,
                    FullName = name,
                    Kind = kind,
                    FilePath = filePath,
                    Line = line,
                    IndexedAt = now
                };
            }
        }
    }
}
