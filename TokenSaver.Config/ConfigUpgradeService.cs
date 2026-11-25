using System.Linq;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace AiDevTools.TokenSaver.Config;

/// <summary>
///     Patches an existing <c>config.json</c> (local or global) that predates a schema change —
///     e.g. a file created by an older TokenSaver version, before a section, a nested object, or
///     even a single leaf setting was added to <see cref="TokenSaverBehaviorConfig" />.
///     Complements <see cref="WorkspaceConfigDefaults.EnsureDefaults" />: that method handles a
///     config file that's missing entirely; this one handles a config file that exists but whose
///     structure — at any depth — predates the current <see cref="ConfigSchemaTemplates" /> tree.
///     <para>
///     Runs alongside <see cref="WorkspaceConfigDefaults.EnsureDefaults" /> on every server
///     startup and MCP session connect. It walks <see cref="ConfigSchemaTemplates.RootElements" />
///     against the file's actual structure (via a raw <see cref="Utf8JsonReader" /> pass so byte
///     positions — and therefore untouched surrounding text/comments — are exact): for every node
///     the file is missing, whether that's a whole top-level section, a nested object
///     (<c>codeHelper.ownership</c>), or a single leaf inside an object that otherwise already
///     exists (<c>codeHelper.requireAnchorMatch</c>), it splices that node's template block in
///     right before its parent object's closing brace. A node found present but itself an object
///     is recursed into to find anything missing further down; a node found present is otherwise
///     left completely untouched, comments and all — only whole missing nodes are ever added,
///     never merged into or reformatting an existing one.
///     </para>
///     Idempotent and best-effort: an I/O or parse failure is logged and swallowed so a malformed
///     or locked config file never blocks startup or a session handshake.
/// </summary>
public static partial class ConfigUpgradeService
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Used only for the local-file leaf-documented check (see LeafDocumented): comments must be
    // surfaced as tokens, not silently skipped, so a commented-out leaf line can be recognized as
    // "already documented" rather than "missing".
    private static readonly JsonReaderOptions ReaderOptionsAllowComments = new()
    {
        CommentHandling = JsonCommentHandling.Allow,
        AllowTrailingCommas = true,
    };

    /// <summary>Upgrades both the local (<paramref name="workspaceRoot" />) and machine-global config files, if they exist.</summary>
    public static void UpgradeIfNeeded(string workspaceRoot, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        UpgradeFile(BehaviorConfigService.LocalConfigPath(workspaceRoot), commented: true, logger);
        UpgradeFile(BehaviorConfigService.GlobalConfigPath, commented: false, logger);
    }

    private static void UpgradeFile(string path, bool commented, ILogger logger)
    {
        // A missing file is WorkspaceConfigDefaults.EnsureDefaults's job — it always creates a
        // fully up-to-date file, so there is nothing to patch here.
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            byte[] utf8 = File.ReadAllBytes(path);
            if (utf8.Length == 0 || !TryGetObjectSpan(utf8, [], out _, out _))
            {
                // Empty or not a JSON object at the root — leave it alone; ConfigUpgradeService
                // is not a repair tool for malformed config.
                return;
            }

            List<(int Offset, string Text)> insertions = [];
            CollectMissing(utf8, [], ConfigSchemaTemplates.RootElements, commented, insertions);

            if (insertions.Count == 0)
            {
                return;
            }

            byte[] updated = ApplyInsertions(utf8, insertions);
            File.WriteAllBytes(path, updated);
            LogNodesAdded(logger, path, insertions.Count);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LogUpgradeFailed(logger, path, ex);
        }
    }

    /// <summary>
    ///     Recursively finds every <paramref name="elements" /> node missing from the object at
    ///     <paramref name="parentPath" />, queuing an insertion (at that parent's closing-brace
    ///     byte offset) for each. A present node with children is recursed into; a present leaf
    ///     (any value shape — <see cref="ConfigElementTemplate.Children" /> is null) is left alone.
    /// </summary>
    private static void CollectMissing(
        byte[] utf8, IReadOnlyList<string> parentPath, IReadOnlyList<ConfigElementTemplate> elements,
        bool commented, List<(int Offset, string Text)> insertions)
    {
        if (!TryGetObjectSpan(utf8, parentPath, out _, out int parentCloseIndex))
        {
            // Parent itself doesn't exist in the file — callers only ever recurse into a parent
            // they just confirmed exists, so this should not happen; bail out defensively.
            return;
        }

        foreach (ConfigElementTemplate element in elements)
        {
            List<string> childPath = [.. parentPath, element.Key];

            if (element.Children is { Count: > 0 })
            {
                if (TryGetObjectSpan(utf8, childPath, out _, out _))
                {
                    CollectMissing(utf8, childPath, element.Children, commented, insertions);
                }
                else
                {
                    insertions.Add((parentCloseIndex, commented ? element.CommentedBlock : element.UncommentedBlock));
                }
            }
            else if (!element.SentinelNeverActive)
            {
                // The local template writes every leaf as a comment by design (see
                // WorkspaceConfigDefaults) — so on that side "already there" means "the key text
                // appears anywhere under this parent, live or commented", not "is live JSON".
                // The global template always writes leaves active, so a structural (live-only)
                // check is correct and sufficient there.
                bool alreadyPresent = commented
                    ? LeafDocumented(utf8, parentPath, element.Key)
                    : PropertyExists(utf8, parentPath, element.Key);

                if (!alreadyPresent)
                {
                    insertions.Add((parentCloseIndex, commented ? element.CommentedBlock : element.UncommentedBlock));
                }
            }
        }
    }

    /// <summary>
    ///     Applies queued insertions to <paramref name="utf8" />, working from the highest byte
    ///     offset down so earlier offsets — computed once, up front, against the untouched
    ///     original bytes — stay valid as the buffer grows. Insertions sharing a parent (and
    ///     therefore the same offset) are concatenated in schema order. Operates on raw UTF-8
    ///     bytes throughout (never a decoded string) because this codebase's own template
    ///     comments contain multi-byte characters (e.g. em dashes) — a char-index insertion would
    ///     silently misplace text whenever one preceded the insertion point.
    /// </summary>
    private static byte[] ApplyInsertions(byte[] utf8, List<(int Offset, string Text)> insertions)
    {
        List<byte> result = [.. utf8];

        foreach (IGrouping<int, (int Offset, string Text)> group in insertions
                     .GroupBy(i => i.Offset)
                     .OrderByDescending(g => g.Key))
        {
            string joined = string.Join("\n\n", group.Select(g => g.Text));
            bool needsComma = NeedsLeadingComma(result, group.Key);
            string insertion = (needsComma ? "," : "") + "\n\n" + joined + "\n";
            result.InsertRange(group.Key, Encoding.UTF8.GetBytes(insertion));
        }

        return [.. result];
    }

    /// <summary>Scans backward from a closing-brace offset, skipping whitespace, to decide whether a comma is needed before a new property.</summary>
    private static bool NeedsLeadingComma(List<byte> buffer, int closeBraceOffset)
    {
        for (int i = closeBraceOffset - 1; i >= 0; i--)
        {
            byte b = buffer[i];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                continue;
            }

            return b != (byte)'{' && b != (byte)',';
        }

        return false;
    }

    // ── Structural lookups (Utf8JsonReader, byte-offset precise, comment-skipping) ────────────

    /// <summary>
    ///     Locates the object at <paramref name="path" /> (empty = document root) and returns the
    ///     byte offsets of its opening <c>{</c> and matching closing <c>}</c>. Every path segment
    ///     must itself be an object; a path segment present with a non-object value, or absent
    ///     entirely, returns <see langword="false" />.
    /// </summary>
    private static bool TryGetObjectSpan(byte[] utf8, IReadOnlyList<string> path, out int openIndex, out int closeIndex)
    {
        openIndex = -1;
        closeIndex = -1;

        Utf8JsonReader reader = new(utf8, isFinalBlock: true, state: new JsonReaderState(ReaderOptions));
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        if (path.Count == 0)
        {
            openIndex = 0;
            int rootDepth = 1;
            while (rootDepth > 0 && reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    rootDepth++;
                }
                else if (reader.TokenType == JsonTokenType.EndObject)
                {
                    rootDepth--;
                }
            }

            closeIndex = (int)reader.TokenStartIndex;
            return rootDepth == 0;
        }

        int targetDepth = 1;
        int segmentIndex = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName &&
                reader.CurrentDepth == targetDepth &&
                reader.ValueTextEquals(path[segmentIndex]))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                {
                    return false;
                }

                segmentIndex++;
                openIndex = (int)reader.TokenStartIndex;

                if (segmentIndex == path.Count)
                {
                    int depth = 1;
                    while (depth > 0 && reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.StartObject)
                        {
                            depth++;
                        }
                        else if (reader.TokenType == JsonTokenType.EndObject)
                        {
                            depth--;
                        }
                    }

                    closeIndex = (int)reader.TokenStartIndex;
                    return depth == 0;
                }

                targetDepth++;
                continue;
            }

            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth < targetDepth)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="key" /> exists as a direct property of the object at <paramref name="parentPath" />, regardless of its value's JSON type.</summary>
    private static bool PropertyExists(byte[] utf8, IReadOnlyList<string> parentPath, string key)
    {
        Utf8JsonReader reader = new(utf8, isFinalBlock: true, state: new JsonReaderState(ReaderOptions));
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        int targetDepth = 1;
        for (int i = 0; i < parentPath.Count; i++)
        {
            bool matched = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName &&
                    reader.CurrentDepth == targetDepth &&
                    reader.ValueTextEquals(parentPath[i]))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                    {
                        return false;
                    }

                    matched = true;
                    break;
                }

                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth < targetDepth)
                {
                    return false;
                }
            }

            if (!matched)
            {
                return false;
            }

            targetDepth++;
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName &&
                reader.CurrentDepth == targetDepth &&
                reader.ValueTextEquals(key))
            {
                return true;
            }

            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth < targetDepth)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    ///     Local-file counterpart to <see cref="PropertyExists" />: whether <paramref name="key" />
    ///     is already documented as a direct child of the object at <paramref name="parentPath" />
    ///     — either as a live JSON property, or as a commented-out <c>// "key": value</c> line at
    ///     the same nesting depth (this codebase's own convention for an unset local override; see
    ///     <see cref="WorkspaceConfigDefaults" />). Reads with <see cref="JsonCommentHandling.Allow" />
    ///     so comment text is inspected rather than discarded, while non-comment tokens still drive
    ///     normal structural/depth tracking.
    /// </summary>
    private static bool LeafDocumented(byte[] utf8, IReadOnlyList<string> parentPath, string key)
    {
        Utf8JsonReader reader = new(utf8, isFinalBlock: true, state: new JsonReaderState(ReaderOptionsAllowComments));
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        int targetDepth = 1;
        for (int i = 0; i < parentPath.Count; i++)
        {
            bool matched = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName &&
                    reader.CurrentDepth == targetDepth &&
                    reader.ValueTextEquals(parentPath[i]))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                    {
                        return false;
                    }

                    matched = true;
                    break;
                }

                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth < targetDepth)
                {
                    return false;
                }
            }

            if (!matched)
            {
                return false;
            }

            targetDepth++;
        }

        string needle = $"\"{key}\"";
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth < targetDepth)
            {
                return false;
            }

            if (reader.CurrentDepth != targetDepth)
            {
                continue;
            }

            if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals(key))
            {
                return true;
            }

            if (reader.TokenType == JsonTokenType.Comment && reader.GetComment().Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Added {Count} missing config node(s) to {Path}")]
    private static partial void LogNodesAdded(ILogger logger, string path, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to upgrade config at {Path}; leaving it as-is")]
    private static partial void LogUpgradeFailed(ILogger logger, string path, Exception ex);
}
