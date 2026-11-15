using System.Text.RegularExpressions;

using TokenSaver.Database.Context.Entities;

namespace TokenSaver.CodeHelper.Indexing;

/// <summary>
///     Builds symbol-to-symbol edges for C# files (rows for <see cref="SymbolReferenceEntity" />).
///     Heuristic, regex-based, no Roslyn: two edge kinds, both from a file's top-level types to
///     top-level types in other files.
///     <list type="bullet">
///         <item><description><see cref="KindTypeUsage" />: the other type's name appears as an identifier in the file.</description></item>
///         <item><description><see cref="KindUsing" />: the file has <c>using Ns;</c> and the other type is declared in namespace <c>Ns</c>.</description></item>
///     </list>
///     Name collisions produce false edges; that is accepted for a lightweight index.
/// </summary>
internal static partial class CSharpReferenceGraph
{
    public const int KindTypeUsage = 1;
    public const int KindUsing = 2;

    /// <summary>A top-level type declared in a file: the index symbol id, its file, and its name.</summary>
    public readonly record struct TypeNode(string SymbolId, string FilePath, string Name);

    public static List<SymbolReferenceEntity> Build(
        IReadOnlyList<TypeNode> topLevelTypes,
        IReadOnlyDictionary<string, string> csharpContentByPath)
    {
        Dictionary<string, List<TypeNode>> typesByFile = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<TypeNode>> typesByName = new(StringComparer.Ordinal);
        foreach (TypeNode t in topLevelTypes)
        {
            AddTo(typesByFile, t.FilePath, t);
            AddTo(typesByName, t.Name, t);
        }

        Dictionary<string, List<TypeNode>> typesByNamespace = new(StringComparer.Ordinal);
        Dictionary<string, HashSet<string>> usingsByFile = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, HashSet<string>> identifiersByFile = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string path, string content) in csharpContentByPath)
        {
            if (!typesByFile.ContainsKey(path))
            {
                continue;
            }

            string? ns = NamespaceRegex().Match(content) is { Success: true } nm ? nm.Groups[1].Value : null;
            if (ns != null)
            {
                foreach (TypeNode t in typesByFile[path])
                {
                    AddTo(typesByNamespace, ns, t);
                }
            }

            usingsByFile[path] = UsingRegex().Matches(content).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            identifiersByFile[path] = IdentifierRegex().Matches(content).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        }

        HashSet<(string Src, string Tgt, int Kind)> seen = [];
        List<SymbolReferenceEntity> edges = [];

        foreach ((string path, List<TypeNode> sources) in typesByFile)
        {
            if (!identifiersByFile.TryGetValue(path, out HashSet<string>? identifiers))
            {
                continue;
            }

            HashSet<string> usings = usingsByFile.GetValueOrDefault(path) ?? [];

            foreach (string id in identifiers)
            {
                if (!typesByName.TryGetValue(id, out List<TypeNode>? named))
                {
                    continue;
                }

                foreach (TypeNode target in named)
                {
                    AddEdges(sources, target, KindTypeUsage, seen, edges);
                }
            }

            foreach (string ns in usings)
            {
                if (!typesByNamespace.TryGetValue(ns, out List<TypeNode>? inNamespace))
                {
                    continue;
                }

                foreach (TypeNode target in inNamespace)
                {
                    AddEdges(sources, target, KindUsing, seen, edges);
                }
            }
        }

        return edges;
    }

    private static void AddEdges(
        List<TypeNode> sources, TypeNode target, int kind,
        HashSet<(string Src, string Tgt, int Kind)> seen, List<SymbolReferenceEntity> edges)
    {
        foreach (TypeNode source in sources)
        {
            if (string.Equals(source.FilePath, target.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!seen.Add((source.SymbolId, target.SymbolId, kind)))
            {
                continue;
            }

            edges.Add(new SymbolReferenceEntity
            {
                SourceSymbolId = source.SymbolId,
                TargetSymbolId = target.SymbolId,
                Kind = kind,
                Line = 0,
            });
        }
    }

    private static void AddTo<TKey, TValue>(Dictionary<TKey, List<TValue>> map, TKey key, TValue value)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out List<TValue>? list))
        {
            list = [];
            map[key] = list;
        }

        list.Add(value);
    }

    [GeneratedRegex(@"^\s*namespace\s+([\w.]+)\s*[;{]", RegexOptions.Multiline)]
    private static partial Regex NamespaceRegex();

    [GeneratedRegex(@"^\s*using\s+(?!static\s)([\w.]+)\s*;", RegexOptions.Multiline)]
    private static partial Regex UsingRegex();

    [GeneratedRegex(@"\b[A-Za-z_]\w*\b")]
    private static partial Regex IdentifierRegex();
}
