using System.Collections.ObjectModel;

namespace TokenSaver.ContextHelper.Models.Diagnostics;

public class ContextManifest
{
    public Collection<ManifestEntry> Entries { get; } = [];

    public IEnumerable<ManifestEntry> Filter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return Entries;
        }

        string normalized = filter.Trim().ToUpperInvariant();
        return normalized switch
        {
            "CODE" => Entries.Where(e => e.Kind == ContentKind.Code),
            "LOGS" => Entries.Where(e => e.Kind == ContentKind.Log),
            "FILES" => Entries.Where(e => e.Kind == ContentKind.File),
            "CONVERSATION" => Entries.Where(e => e.Kind == ContentKind.Conversation),
            "TOOLCALLS" => Entries.Where(e => e.Kind == ContentKind.ToolCall),
            _ => Entries.Where(e =>
                e.Source.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                e.SectionName.Contains(normalized, StringComparison.OrdinalIgnoreCase))
        };
    }
}
