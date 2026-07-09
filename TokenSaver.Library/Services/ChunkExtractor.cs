using TokenSaver.Library.Contracts;
using TokenSaver.Library.Models.TokenEconomy;

using System.Text.RegularExpressions;

namespace TokenSaver.Library.Services;

/// <summary>Default <see cref="IChunkExtractor" /> — see that interface's doc comment for scope/rationale.</summary>
public sealed partial class ChunkExtractor : IChunkExtractor
{
    // .NET regex supports the same named group ("name") repeated across different alternation
    // branches of one pattern — each branch only ever matches one line, so there's no ambiguity
    // about which branch's capture is "the" name for that match.
    [GeneratedRegex(@"^\s*(export\s+)?(async\s+)?function\s+(?<name>\w+)|^\s*(export\s+)?class\s+(?<name>\w+)|^\s*const\s+(?<name>\w+)\s*=\s*(async\s*)?\(", RegexOptions.None)]
    private static partial Regex JsBoundary();

    [GeneratedRegex(@"^\s*(export\s+)?(async\s+)?function\s+(?<name>\w+)|^\s*(export\s+)?class\s+(?<name>\w+)", RegexOptions.None)]
    private static partial Regex TsBoundary();

    [GeneratedRegex(@"^\s*(async\s+)?def\s+(?<name>\w+)|^\s*class\s+(?<name>\w+)", RegexOptions.None)]
    private static partial Regex PyBoundary();

    // C# isn't in the original plan's language set — added since this codebase is primarily C#.
    // Two branches: a type declaration (class/interface/record/struct/enum), or a
    // method-looking signature (modifiers, a return type, a name, then an open paren).
    // Heuristic, like the others: can false-positive on e.g. a property with a parenthesized
    // default-value expression — acceptable for chunking granularity, not a correctness parser.
    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]\s*)*(?:public|private|protected|internal|static|sealed|abstract|readonly|virtual|override|async|partial|\s)*\b(?:class|interface|record|struct|enum)\s+(?<name>\w+)|^\s*(?:public|private|protected|internal|static|async|virtual|override|\s)+[\w<>\[\],.?]+\s+(?<name>\w+)\s*\(", RegexOptions.None)]
    private static partial Regex CsBoundary();

    [GeneratedRegex(@"^(#{1,6})\s+(.*)", RegexOptions.None)]
    private static partial Regex MarkdownHeading();

    public IReadOnlyList<CodeChunk> ExtractChunks(string filePath, string content)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".md" || ext == ".markdown")
        {
            return ChunkMarkdown(content);
        }

        Regex? boundary = ext switch
        {
            ".js" or ".jsx" or ".mjs" or ".cjs" => JsBoundary(),
            ".ts" or ".tsx" => TsBoundary(),
            ".py" => PyBoundary(),
            ".cs" => CsBoundary(),
            _ => null
        };

        return boundary is null ? [WholeFileModule(filePath, content)] : ChunkCode(filePath, content, boundary);
    }

    private static CodeChunk WholeFileModule(string filePath, string content)
    {
        int lineCount = Math.Max(1, content.Split('\n').Length);
        return new CodeChunk("module", Path.GetFileName(filePath), 1, lineCount, content);
    }

    private static List<CodeChunk> ChunkCode(string filePath, string content, Regex boundary)
    {
        string[] lines = content.Split('\n');
        List<(int Line, string Name)> boundaries = [];

        for (int i = 0; i < lines.Length; i++)
        {
            Match m = boundary.Match(lines[i]);
            if (m.Success)
            {
                string name = m.Groups["name"].Success ? m.Groups["name"].Value : "anonymous";
                boundaries.Add((i + 1, name));
            }
        }

        if (boundaries.Count == 0)
        {
            return [WholeFileModule(filePath, content)];
        }

        List<CodeChunk> chunks = [];
        for (int i = 0; i < boundaries.Count; i++)
        {
            int start = boundaries[i].Line;
            int end = i + 1 < boundaries.Count ? boundaries[i + 1].Line - 1 : lines.Length;
            string text = string.Join('\n', lines[(start - 1)..end]);
            chunks.Add(new CodeChunk("function", boundaries[i].Name, start, end, text));
        }

        return chunks;
    }

    private static List<CodeChunk> ChunkMarkdown(string content)
    {
        string[] lines = content.Split('\n');
        List<(int Line, int Level, string Text)> headings = [];

        for (int i = 0; i < lines.Length; i++)
        {
            Match m = MarkdownHeading().Match(lines[i]);
            if (m.Success)
            {
                headings.Add((i + 1, m.Groups[1].Value.Length, m.Groups[2].Value.Trim()));
            }
        }

        if (headings.Count == 0)
        {
            return [new CodeChunk("md_section", "(no headings)", 1, Math.Max(1, lines.Length), content)];
        }

        List<CodeChunk> chunks = [];
        for (int i = 0; i < headings.Count; i++)
        {
            int start = headings[i].Line;
            int end = lines.Length;
            for (int j = i + 1; j < headings.Count; j++)
            {
                if (headings[j].Level <= headings[i].Level)
                {
                    end = headings[j].Line - 1;
                    break;
                }
            }

            string text = string.Join('\n', lines[(start - 1)..end]);
            chunks.Add(new CodeChunk("md_section", headings[i].Text, start, end, text));
        }

        return chunks;
    }
}
