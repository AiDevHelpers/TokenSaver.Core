namespace TokenSaver.Library.Models.TokenEconomy;

public sealed record RepoIndexStats(
    int FilesScanned,
    int ChunksIndexed,
    int ChunksSkipped,
    int ChunksDeleted,
    TimeSpan Elapsed);
