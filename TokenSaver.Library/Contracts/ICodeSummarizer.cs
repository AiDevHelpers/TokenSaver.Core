namespace TokenSaver.Library.Contracts;

/// <summary>
///     Generates a short natural-language summary of a source file's purpose and key
///     declarations.  Implementations may call a local AI model (draft or main) and
///     should cache results to avoid re-summarising unchanged files.
///     Injected optionally into <see cref="Services.LocalContextService" />; when null
///     (or when the implementation returns null) the service falls back to the static
///     <c>ExtractStaticSummaryAsync</c> extraction.
/// </summary>
public interface ICodeSummarizer
{
    /// <summary>
    ///     Returns a one-to-two sentence summary suitable for a workspace index entry,
    ///     or null if the file type is unsupported, the model is unavailable, or an
    ///     error occurs (caller should fall back to static extraction).
    /// </summary>
    /// <param name="fileContent">Raw source text (callers may truncate to ~4 KB before passing).</param>
    /// <param name="fileName">File name including extension, used to tailor the prompt.</param>
    /// <param name="fileHash">Stable hash of <paramref name="fileContent" /> used as a cache key.</param>
    /// <param name="preferBackupEndpoint">
    ///     When true, hints the implementation to route this call to a non-primary healthy
    ///     Ollama endpoint if one is configured and available, rather than the primary. Intended
    ///     for background/bulk indexing so it doesn't compete with interactive requests for the
    ///     primary endpoint's capacity. Implementations without multiple endpoints ignore this.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<string?> SummarizeAsync(
        string fileContent,
        string fileName,
        string fileHash,
        bool preferBackupEndpoint = false,
        CancellationToken ct = default);
}
