namespace AiDevTools.TokenSaver.TokenEconomy.Models.TokenEconomy;

public sealed record RepoIndexStats(
    int FilesScanned,
    int ChunksIndexed,
    int ChunksSkipped,
    int ChunksDeleted,
    TimeSpan Elapsed,
    int ChunksFailed = 0,
    int FilesRead = 0);
