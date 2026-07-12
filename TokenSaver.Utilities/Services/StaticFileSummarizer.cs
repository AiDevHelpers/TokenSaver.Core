using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using static System.StringComparison;

namespace TokenSaver.Utilities.Services;

/// <summary>
///     Extracts a short human-readable description of a file from its content without calling any
///     AI model — used as the fallback (or the only source, for extensions the summarizer doesn't
///     cover) when building each File row's Description. Ported unchanged from the old
///     LocalContextService.ExtractStaticSummaryAsync.
/// </summary>
public static class StaticFileSummarizer
{
    public static async Task<string?> ExtractAsync(string filePath)
    {
        try
        {
            string ext = Path.GetExtension(filePath).ToUpperInvariant();

            const int MaxBytes = 4096;
            await using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] buf = new byte[MaxBytes];
            int read = await fs.ReadAsync(buf.AsMemory(0, MaxBytes));
            string content = Encoding.UTF8.GetString(buf, 0, read);
            string[] lines = content.Split('\n');

            return ext switch
            {
                ".MD" or ".MDX" => ExtractMarkdownHeadline(lines),
                ".CS" => ExtractCSharpSummary(lines),
                ".TS" or ".TSX" or ".JS" or ".JSX" => ExtractJsSummary(lines),
                ".JSON" => ExtractJsonSummary(content),
                ".CSPROJ" or ".VBPROJ" or ".FSPROJ" => Path.GetFileNameWithoutExtension(filePath) + " (.NET project)",
                ".SLN" or ".SLNX" => Path.GetFileNameWithoutExtension(filePath) + " (solution)",
                ".SQL" => ExtractFirstMeaningfulLine(lines, "--"),
                ".PY" => ExtractPythonSummary(lines),
                "" => IsProbablyBinary(buf, read)
                    ? "binary file, no extension"
                    : ExtractFirstMeaningfulLine(lines, null),
                _ => IsProbablyBinary(buf, read)
                    ? $"binary file ({ext.TrimStart('.')})"
                    : ExtractFirstMeaningfulLine(lines, null)
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Cheap binary sniff: a NUL byte in the first 4KB is a strong binary signal.
    ///     Used so "index every file" doesn't try to line-split images/executables as text.
    /// </summary>
    private static bool IsProbablyBinary(byte[] buf, int read)
    {
        for (int i = 0; i < read; i++)
        {
            if (buf[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string? ExtractMarkdownHeadline(string[] lines)
    {
        foreach (string line in lines)
        {
            string t = line.Trim();
            if (t.StartsWith('#'))
            {
                return t.TrimStart('#').Trim();
            }
        }

        return ExtractFirstMeaningfulLine(lines, null);
    }

    private static string? ExtractCSharpSummary(string[] lines)
    {
        List<string> summaryParts = [];
        bool inSummary = false;
        string? typeDecl = null;
        string? ns = null;

        foreach (string rawLine in lines)
        {
            string t = rawLine.Trim();

            if (t.StartsWith("/// <summary>", InvariantCulture))
            {
                inSummary = true;
                continue;
            }

            if (t.StartsWith("/// </summary>", InvariantCulture))
            {
                inSummary = false;
                continue;
            }

            if (inSummary && t.StartsWith("///", InvariantCulture))
            {
                summaryParts.Add(t.TrimStart('/').Trim());
                continue;
            }

            if (ns == null && t.StartsWith("namespace ", InvariantCulture))
            {
                ns = t["namespace ".Length..].TrimEnd(';', '{', ' ');
            }

            if (typeDecl == null)
            {
                string lower = t.ToUpperInvariant();
                if ((lower.Contains("PUBLIC ", InvariantCulture) || lower.Contains("INTERNAL ", InvariantCulture)) &&
                    (lower.Contains(" CLASS ", InvariantCulture) || lower.Contains(" INTERFACE ", InvariantCulture) ||
                     lower.Contains(" RECORD ", InvariantCulture) || lower.Contains(" ENUM ", InvariantCulture) ||
                     lower.Contains(" STRUCT ", InvariantCulture)))
                {
                    typeDecl = Regex.Replace(
                            t, @"\b(PUBLIC|INTERNAL|PRIVATE|PROTECTED|SEALED|ABSTRACT|STATIC|PARTIAL|READONLY)\b\s*",
                            "")
                        .Split('{', ':')[0].Trim();
                }
            }
        }

        string? docSummary = summaryParts.Count > 0 ? string.Join(" ", summaryParts).Trim() : null;
        return docSummary != null ? Truncate(docSummary, 120) : typeDecl != null ? ns != null ? $"{typeDecl} in {ns}" : typeDecl : null;
    }

    private static string? ExtractJsSummary(string[] lines)
    {
        List<string> exports = [];
        bool inJsDoc = false;
        List<string> jsDocParts = [];

        foreach (string rawLine in lines)
        {
            string t = rawLine.Trim();
            if (t.StartsWith("/**", InvariantCulture))
            {
                inJsDoc = true;
                continue;
            }

            if (t.StartsWith("*/", InvariantCulture))
            {
                inJsDoc = false;
                continue;
            }

            if (inJsDoc && "*".StartsWith(t, InvariantCulture) && !t.StartsWith("* @", InvariantCulture))
            {
                jsDocParts.Add(t.TrimStart('*').Trim());
            }

            if (t.StartsWith("export ", InvariantCulture))
            {
                string decl = t["export ".Length..].TrimStart();
                if (decl.StartsWith("default ", InvariantCulture))
                {
                    decl = decl["default ".Length..].TrimStart();
                }

                string kind = decl.Split(' ')[0];
                Match match = Regex.Match(decl, @"\b(\w+)\s*[\(<{=]");
                if (match.Success)
                {
                    exports.Add($"{kind} {match.Groups[1].Value}");
                }

                if (exports.Count >= 4)
                {
                    break;
                }
            }
        }

        string? doc = jsDocParts.Count > 0 ? string.Join(" ", jsDocParts.Where(p => p.Length > 0)).Trim() : null;
        return doc != null && doc.Length > 5 ? Truncate(doc, 120) : exports.Count > 0 ? "Exports: " + string.Join(", ", exports) : null;
    }

    private static string? ExtractJsonSummary(string content)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(content);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                string[] keys = root.EnumerateObject().Take(6).Select(p => p.Name).ToArray();
                return $"Object: {string.Join(", ", keys)}{(root.EnumerateObject().Skip(6).Any() ? ", …" : "")}";
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                return $"Array ({root.GetArrayLength()} items)";
            }
        }
        catch
        {
            /* not valid JSON, or truncated at 4KB */
        }

        return null;
    }

    private static string? ExtractPythonSummary(string[] lines)
    {
        List<string> classes = [];
        List<string> funcs = [];
        string? docstring = null;

        foreach (string rawLine in lines)
        {
            string t = rawLine.Trim();
            if (docstring == null && !(!t.StartsWith("\"\"\"", StringComparison.InvariantCulture) && !t.StartsWith("'''", StringComparison.InvariantCulture)))
            {
                docstring = t.TrimStart('"').TrimStart('\'').Trim();
                if (docstring.Length > 0)
                {
                    break;
                }

                continue;
            }

            if (t.StartsWith("class ", StringComparison.InvariantCulture))
            {
                classes.Add(t["class ".Length..].Split('(', ':')[0].Trim());
            }
            else if (t.StartsWith("def ", StringComparison.InvariantCulture) || t.StartsWith("async def ", StringComparison.InvariantCulture))
            {
                funcs.Add(t.Replace("async def ", "", StringComparison.InvariantCulture).Replace("def ", "", StringComparison.InvariantCulture).Split('(')[0].Trim());
            }

            if (classes.Count + funcs.Count >= 5)
            {
                break;
            }
        }

        if (docstring != null && docstring.Length > 5)
        {
            return Truncate(docstring, 120);
        }

        List<string> parts = [];
        if (classes.Count > 0)
        {
            parts.Add($"Classes: {string.Join(", ", classes)}");
        }

        if (funcs.Count > 0)
        {
            parts.Add($"Functions: {string.Join(", ", funcs.Take(3))}{(funcs.Count > 3 ? ", …" : "")}");
        }

        return parts.Count > 0 ? string.Join("; ", parts) : null;
    }

    private static string? ExtractFirstMeaningfulLine(string[] lines, string? commentPrefix)
    {
        return (from rawLine in lines select rawLine.Trim() into t where t.Length != 0 where commentPrefix == null || !t.StartsWith(commentPrefix, StringComparison.InvariantCulture) select Truncate(t, 120)).FirstOrDefault();
    }

    private static string Truncate(string s, int max)
    {
        return s.Length <= max ? s : s[..max].TrimEnd() + "…";
    }
}
