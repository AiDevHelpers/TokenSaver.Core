namespace TokenSaver.WslControl.Contracts;

/// <summary>
///     Abstraction over the local model used to translate natural-language requests into WSL
///     commands. Declared here so <c>TokenSaver.WslControl</c> stays free of a dependency on
///     <c>TokenSaver.OllamaIntegration</c>; the host wires an adapter that forwards to the
///     configured local model (the DraftModel/Ollama router).
/// </summary>
public interface IWslCommandModel
{
    /// <summary>
    ///     Completes <paramref name="userPrompt" /> under <paramref name="systemPrompt" /> and
    ///     returns the raw model response. Implementations should route to the fast local model.
    /// </summary>
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
}
