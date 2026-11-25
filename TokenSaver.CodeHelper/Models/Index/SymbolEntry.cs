namespace AiDevTools.TokenSaver.CodeHelper.Models.Index;

public record SymbolEntry
{
    /// <summary>Stable id: SHA1 of (FilePath + Name + Line).</summary>
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>Fully-qualified name, e.g. MyNs.MyClass.MyMethod.</summary>
    public string FullName { get; init; } = "";

    public SymbolKind Kind { get; init; }

    /// <summary>Workspace-relative path.</summary>
    public string FilePath { get; init; } = "";

    public int Line { get; init; }
    public int Column { get; init; }
    public string? ContainingType { get; init; }

    /// <summary>XML doc summary or inferred description.</summary>
    public string? Summary { get; init; }

    /// <summary>Unix milliseconds when this entry was last indexed.</summary>
    public long IndexedAt { get; init; }
}
