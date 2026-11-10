using TokenSaver.Database.Context;
using TokenSaver.TokenEconomy.Contracts;
using TokenSaver.TokenEconomy.Models.TokenEconomy;

using System.Globalization;
using System.Text;

namespace TokenSaver.TokenEconomy.Services;

/// <summary>Result of <see cref="IndexSelfTest.RunAsync" />: what the known fixture produced.</summary>
public sealed record IndexSelfTestResult(
    bool Passed,
    int FilesExpected,
    int FilesScanned,
    int ChunksIndexed,
    int ChunksStored,
    int ChunksFailed,
    string Narrative);

/// <summary>
///     Indexes a small known fixture in a scratch folder and checks that the run scanned every
///     supported file, stored exactly the chunks it reported, and failed none. The fixture has
///     three supported files and one unsupported file, so a scanner that drops files or stores
///     the wrong number of chunks fails here instead of silently in production.
/// </summary>
public sealed class IndexSelfTest(IRepoIndexer indexer, TokenEconomyDbAccessor db)
{
    private const int ExpectedFiles = 3;

    public async Task<IndexSelfTestResult> RunAsync(CancellationToken ct = default)
    {
        string root = Path.Combine(Path.GetTempPath(), "tokensaver-indexselftest-" + Guid.NewGuid());
        try
        {
            WriteFixture(root);
            RepoIndexStats stats = await indexer.IndexRepoAsync(root, ct: ct);
            WorkspaceIndexContext ctx = await db.GetDbAsync(root, ct);
            int stored = await db.WithDbLockAsync(() => Task.FromResult(ctx.Chunks.ToList().Count), ct);

            bool passed = stats.FilesScanned == ExpectedFiles
                && stats.ChunksFailed == 0
                && stats.ChunksIndexed > 0
                && stored == stats.ChunksIndexed;

            StringBuilder sb = new();
            _ = sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "files scanned {0} of {1} expected", stats.FilesScanned, ExpectedFiles));
            _ = sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "chunks indexed {0}, stored {1}, failed {2}", stats.ChunksIndexed, stored, stats.ChunksFailed));

            return new IndexSelfTestResult(passed, ExpectedFiles, stats.FilesScanned,
                stats.ChunksIndexed, stored, stats.ChunksFailed, sb.ToString().TrimEnd());
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // best-effort: the SQLite handle may still be open on Windows; the folder is in temp
            }
        }
    }

    private static void WriteFixture(string root)
    {
        _ = Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "A.cs"),
            "namespace Foo;\n\npublic sealed class A\n{\n    public void One() { }\n}\n");
        File.WriteAllText(Path.Combine(root, "sub", "B.cs"),
            "namespace Foo;\n\npublic sealed class B\n{\n    public void Two() { }\n}\n");
        File.WriteAllText(Path.Combine(root, "doc.md"), "# Title\nintro\n## Section A\ncontent A\n");
        File.WriteAllText(Path.Combine(root, "readme.txt"), "not a supported extension");
    }
}
