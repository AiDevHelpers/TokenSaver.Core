using TokenSaver.CodeHelper.Models.Index;

using System.Text.RegularExpressions;

namespace TokenSaver.CodeHelper.Indexing;

/// <summary>
///     Regex-based C# symbol parser.  Handles types (class/interface/struct/enum/record/delegate)
///     and members (methods, constructors, properties, fields, events).
///     No Roslyn dependency — works on any text without SDK/build context.
/// </summary>
public sealed partial class CSharpSymbolParser : ISymbolParser
{
    public IReadOnlyList<string> SupportedExtensions => [".cs"];

    // ─────────────────────────────────────────────────────────────────────────

    public IEnumerable<SymbolEntry> Parse(string filePath, string content)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string[] lines = content.Split('\n');

        string? currentType = null;
        int currentTypeEnd = int.MaxValue;

        // ── Type declarations ──
        foreach (Match m in TypeDeclRegex().Matches(content))
        {
            SymbolKind kind = m.Groups[1].Value switch
            {
                "class" => SymbolKind.Class,
                "interface" => SymbolKind.Interface,
                "struct" => SymbolKind.Struct,
                "enum" => SymbolKind.Enum,
                "record" => SymbolKind.Class, // treat records as classes
                "delegate" => SymbolKind.Delegate,
                _ => SymbolKind.Unknown
            };
            string name = m.Groups[2].Value;
            int line = GetLine(content, m.Index, lines);
            string? summary = CodeParserUtils.ExtractXmlSummary(content, m.Index);

            yield return new SymbolEntry
            {
                Id = CodeParserUtils.MakeId(filePath, kind, name, line),
                Name = name,
                FullName = name,
                Kind = kind,
                FilePath = filePath,
                Line = line,
                IndexedAt = now,
                Summary = summary
            };

            if (currentType == null)
            {
                currentType = name;
                string? block = CodeParserUtils.ExtractBraceBlock(content, m.Index + m.Length - 1);
                currentTypeEnd = block != null ? m.Index + m.Length + block.Length : int.MaxValue;
            }
        }

        // ── Methods ──
        foreach (Match m in MethodRegex().Matches(content))
        {
            string name = m.Groups[3].Value;
            int line = GetLine(content, m.Index, lines);
            string? summary = CodeParserUtils.ExtractXmlSummary(content, m.Index);
            string? inType = m.Index < currentTypeEnd ? currentType : null;

            yield return new SymbolEntry
            {
                Id = CodeParserUtils.MakeId(filePath, SymbolKind.Method, name, line),
                Name = name,
                FullName = inType != null ? $"{inType}.{name}" : name,
                Kind = SymbolKind.Method,
                FilePath = filePath,
                Line = line,
                ContainingType = inType,
                IndexedAt = now,
                Summary = summary
            };
        }

        // ── Properties ──
        foreach (Match m in PropertyRegex().Matches(content))
        {
            string name = m.Groups[2].Value;
            int line = GetLine(content, m.Index, lines);
            string? inType = m.Index < currentTypeEnd ? currentType : null;

            yield return new SymbolEntry
            {
                Id = CodeParserUtils.MakeId(filePath, SymbolKind.Property, name, line),
                Name = name,
                FullName = inType != null ? $"{inType}.{name}" : name,
                Kind = SymbolKind.Property,
                FilePath = filePath,
                Line = line,
                ContainingType = inType,
                IndexedAt = now
            };
        }

        // ── Fields (only private/readonly/const — skip props already matched) ──
        foreach (Match m in FieldRegex().Matches(content))
        {
            string name = m.Groups[2].Value;
            int line = GetLine(content, m.Index, lines);
            string? inType = m.Index < currentTypeEnd ? currentType : null;

            yield return new SymbolEntry
            {
                Id = CodeParserUtils.MakeId(filePath, SymbolKind.Field, name, line),
                Name = name,
                FullName = inType != null ? $"{inType}.{name}" : name,
                Kind = SymbolKind.Field,
                FilePath = filePath,
                Line = line,
                ContainingType = inType,
                IndexedAt = now
            };
        }
    }

    // ── Type declarations ─────────────────────────────────────────────────────

    [GeneratedRegex(
        @"^\s*(?:(?:public|private|protected|internal|static|sealed|abstract|partial|file|readonly|ref|unsafe)\s+)*" +
        @"(class|interface|struct|enum|record|delegate)(?:\s+(?:class|struct))?\s+(\w+)(?:<[^>]+>)?",
        RegexOptions.Multiline)]
    private static partial Regex TypeDeclRegex();

    // ── Methods ───────────────────────────────────────────────────────────────

    [GeneratedRegex(
        @"^\s{4,}((?:(?:public|private|protected|internal|static|async|override|virtual|abstract|sealed|new|partial)\s+)+)" +
        @"(Task(?:<[^>]+>)?|ValueTask(?:<[^>]+>)?|(?:[\w<>\[\]?.]+))\s+(\w+)\s*(\([^)]{0,400}\))\s*(?:=>|where|{|;)",
        RegexOptions.Multiline)]
    private static partial Regex MethodRegex();

    // ── Constructors ──────────────────────────────────────────────────────────

    [GeneratedRegex(
        @"^\s{4,}(?:public|private|protected|internal)\s+(\w+)\s*(\([^)]{0,400}\))\s*(?::|{)",
        RegexOptions.Multiline)]
    private static partial Regex ConstructorRegex();

    // ── Properties ────────────────────────────────────────────────────────────

    [GeneratedRegex(
        @"^\s{4,}(?:(?:public|private|protected|internal|static|virtual|override|abstract|new)\s+)+([\w<>\[\]?.]+)\s+(\w+)\s*\{",
        RegexOptions.Multiline)]
    private static partial Regex PropertyRegex();

    // ── Fields ────────────────────────────────────────────────────────────────

    [GeneratedRegex(
        @"^\s{4,}(?:(?:public|private|protected|internal|static|readonly|const|volatile)\s+)+([\w<>\[\]?.]+)\s+(\w+)\s*[=;]",
        RegexOptions.Multiline)]
    private static partial Regex FieldRegex();

    private static int GetLine(string content, int index, string[] lines)
    {
        string sub = content[..index];
        return sub.Count(c => c == '\n') + 1;
    }
}
