using System.Text;

namespace TokenSaver.LanguageHelpers.Core.Configuration;

/// <summary>Outcome of a <see cref="ConfigBlockWriter" /> upsert.</summary>
public sealed record ConfigBlockResult
{
    public required string Path { get; init; }

    /// <summary>True when the target file did not exist and was created.</summary>
    public bool FileCreated { get; init; }

    /// <summary>True when the managed block was added or its content changed (false = already up to date).</summary>
    public bool Changed { get; init; }
}

/// <summary>
///     Idempotently upserts a marker-delimited "managed block" into a text config file, creating the file
///     if needed and leaving any surrounding content untouched. This is how every language helper writes
///     its analysis configuration (an <c>.editorconfig</c> section, a <c>[tool.ruff]</c> table, a
///     <c>PSScriptAnalyzerSettings.psd1</c>) without clobbering settings the user maintains by hand:
///     content between the <c>begin</c>/<c>end</c> markers is owned by TokenSaver and replaced wholesale;
///     everything else is preserved.
/// </summary>
public static class ConfigBlockWriter
{
    /// <summary>
    ///     Ensures <paramref name="filePath" /> contains exactly one managed block bounded by
    ///     <paramref name="beginMarker" /> / <paramref name="endMarker" /> whose body is
    ///     <paramref name="content" />. Replaces an existing block in place; otherwise appends one. No write
    ///     occurs when the file already matches, so timestamps and watchers stay quiet on repeat calls.
    /// </summary>
    public static async Task<ConfigBlockResult> UpsertAsync(
        string filePath,
        string beginMarker,
        string endMarker,
        string content,
        CancellationToken ct = default)
    {
        bool fileExisted = File.Exists(filePath);
        string existing = fileExisted ? await File.ReadAllTextAsync(filePath, ct) : string.Empty;
        string normalizedContent = content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');

        string block = $"{beginMarker}\n{normalizedContent}\n{endMarker}";
        string normalizedExisting = existing.Replace("\r\n", "\n", StringComparison.Ordinal);

        string updated;
        int begin = normalizedExisting.IndexOf(beginMarker, StringComparison.Ordinal);
        int end = normalizedExisting.IndexOf(endMarker, StringComparison.Ordinal);

        if (begin >= 0 && end > begin)
        {
            // Replace the existing managed region (inclusive of both markers).
            string before = normalizedExisting[..begin].TrimEnd('\n');
            string after = normalizedExisting[(end + endMarker.Length)..].TrimStart('\n');

            StringBuilder sb = new();
            if (before.Length > 0)
            {
                sb.Append(before).Append("\n\n");
            }

            sb.Append(block);
            if (after.Length > 0)
            {
                sb.Append("\n\n").Append(after);
            }

            updated = sb.ToString();
        }
        else
        {
            string trimmed = normalizedExisting.TrimEnd('\n');
            updated = trimmed.Length > 0 ? $"{trimmed}\n\n{block}" : block;
        }

        updated = updated.TrimEnd('\n') + "\n";

        if (fileExisted && normalizedExisting.TrimEnd('\n') + "\n" == updated)
        {
            return new ConfigBlockResult { Path = filePath, FileCreated = false, Changed = false };
        }

        string? dir = System.IO.Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(filePath, updated, ct);
        return new ConfigBlockResult { Path = filePath, FileCreated = !fileExisted, Changed = true };
    }
}
