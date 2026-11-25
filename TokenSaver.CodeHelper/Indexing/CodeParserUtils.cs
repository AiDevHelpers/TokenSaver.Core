using AiDevTools.TokenSaver.CodeHelper.Models.Index;

using System.Security.Cryptography;
using System.Text;

namespace AiDevTools.TokenSaver.CodeHelper.Indexing;

/// <summary>
///     Shared utilities used by all symbol parsers and WorkspaceCodeScanner.
/// </summary>
internal static class CodeParserUtils
{
    /// <summary>
    ///     Starting at <paramref name="startIndex" /> (on or just before the opening '{'),
    ///     finds the matching closing '}' and returns the content between braces.
    ///     Returns null if the block exceeds <paramref name="maxChars" /> or is not found.
    /// </summary>
    public static string? ExtractBraceBlock(string source, int startIndex, int maxChars = 5000)
    {
        int depth = 0;
        int start = -1;

        for (int i = startIndex; i < Math.Min(source.Length, startIndex + maxChars); i++)
        {
            switch (source[i])
            {
                case '{':
                    if (start < 0)
                    {
                        start = i;
                    }

                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        string inner = source[(start + 1)..i].Trim();
                        return inner.Length > maxChars ? null : inner;
                    }

                    break;
            }
        }

        return null;
    }

    /// <summary>
    ///     Extracts the first line of an XML <c>&lt;summary&gt;</c> doc comment immediately
    ///     preceding the given <paramref name="position" /> in the source.
    ///     Returns null if none found within the previous 8 lines.
    /// </summary>
    public static string? ExtractXmlSummary(string source, int position)
    {
        string before = source[..position];
        string[] lines = before.Split('\n');
        for (int i = lines.Length - 1; i >= Math.Max(0, lines.Length - 8); i--)
        {
            string line = lines[i].Trim();
            if (line.StartsWith("/// <summary>", StringComparison.Ordinal))
            {
                string summary = line.Replace("/// <summary>", "", StringComparison.Ordinal).Replace("</summary>", "", StringComparison.Ordinal).Trim();
                if (summary.Length > 0)
                {
                    return summary;
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Stable, non-cryptographic id from file path + symbol kind + name + line number.
    ///     The kind is part of the identity: a type and a member with the same name on the same
    ///     line (e.g. `public interface IFoo { }` also matched as a property) must not share an id,
    ///     or the batch insert fails and the whole file loses its symbols.
    ///     Not used for security purposes — only for deterministic symbol identity in the index.
    ///     Uses SHA-256 (truncated) purely because it's a convenient, collision-resistant,
    ///     built-in stable hash; no cryptographic guarantee is required or relied upon here.
    /// </summary>
    public static string MakeId(string filePath, SymbolKind kind, string name, int line)
    {
        string input = $"{filePath}|{kind}|{name}|{line}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16].ToUpperInvariant();
    }
}
