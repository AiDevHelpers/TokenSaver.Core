namespace TokenSaver.Library.Models.AiProviders;

public record ConfigUpdateResult
{
    public ProviderType Provider { get; init; }

    public bool Success { get; init; }

    /// <summary>Human-readable outcome message.</summary>
    public string Message { get; init; } = "";

    /// <summary>True if the entry was already present and identical — no write was needed.</summary>
    public bool WasAlreadyConfigured { get; init; }

    /// <summary>Absolute path of the file that was written (or would have been written).</summary>
    public string? ConfigPath { get; init; }

    public override string ToString()
    {
        return
            $"{Provider}: {(Success ? WasAlreadyConfigured ? "already configured" : "updated" : "FAILED")} — {Message}";
    }
}
