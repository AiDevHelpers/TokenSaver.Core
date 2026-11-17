using TokenSaver.CodeHelper.Indexing;
using TokenSaver.Database.Context;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Xunit;

namespace TokenSaver.CodeHelper.Tests.Indexing;

/// <summary>
///     Index schema versioning: a database written under another schema version (including the unversioned ones
///     from before versioning) is rebuilt on open; a current one is kept; and one workspace has one cache key.
/// </summary>
public sealed class WorkspaceIndexSchemaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wis-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<long> ScalarAsync(WorkspaceIndexContext ctx, string sql)
    {
        using var conn = new SqliteConnection(ctx.Database.GetConnectionString());
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
#pragma warning disable CA2100 // Test-only helper: every SQL string is a literal in this file.
        cmd.CommandText = sql;
#pragma warning restore CA2100
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<WorkspaceIndexContext> OpenAsync() =>
        await WorkspaceIndexContextFactory.CreateAsync(_root, CancellationToken.None);

    [Fact]
    public async Task FreshIndex_IsStampedWithTheCurrentVersionAndHasEveryTable()
    {
        await using WorkspaceIndexContext ctx = await OpenAsync();

        Assert.Equal(WorkspaceIndexContextFactory.SchemaVersion, await ScalarAsync(ctx, "PRAGMA user_version;"));
        foreach (string table in new[] { "Folders", "Files", "Symbols", "Chunks", "TokenSavings", "RepoStaleness" })
        {
            Assert.Equal(1, await ScalarAsync(ctx, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';"));
        }

        Assert.Equal(1, await ScalarAsync(ctx, "SELECT COUNT(*) FROM sqlite_master WHERE type='view' AND name='folder_overview';"));
    }

    [Fact]
    public async Task AnUnversionedIndex_IsRebuiltOnOpen()
    {
        // Simulate a database from before versioning: current tables, user_version 0, a row that must not survive.
        string dbPath = WorkspaceIndexContextFactory.GetDbPath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        await using (WorkspaceIndexContext old = await OpenAsync())
        {
            _ = await old.Database.ExecuteSqlRawAsync("INSERT INTO \"Folders\" (\"RelativePath\", \"Description\", \"LastModified\", \"UpdatedAt\") VALUES ('stale', '', 0, 0);");
            _ = await old.Database.ExecuteSqlRawAsync("PRAGMA user_version = 0;");
        }

        await using WorkspaceIndexContext rebuilt = await OpenAsync();

        Assert.Equal(WorkspaceIndexContextFactory.SchemaVersion, await ScalarAsync(rebuilt, "PRAGMA user_version;"));
        Assert.Equal(0, await ScalarAsync(rebuilt, "SELECT COUNT(*) FROM \"Folders\";"));
    }

    [Fact]
    public async Task AnIndexFromAnotherVersion_IsRebuiltOnOpen()
    {
        await using (WorkspaceIndexContext first = await OpenAsync())
        {
            _ = await first.Database.ExecuteSqlRawAsync("INSERT INTO \"Folders\" (\"RelativePath\", \"Description\", \"LastModified\", \"UpdatedAt\") VALUES ('stale', '', 0, 0);");
            _ = await first.Database.ExecuteSqlRawAsync($"PRAGMA user_version = {WorkspaceIndexContextFactory.SchemaVersion + 1};");
        }

        await using WorkspaceIndexContext ctx = await OpenAsync();

        Assert.Equal(0, await ScalarAsync(ctx, "SELECT COUNT(*) FROM \"Folders\";"));
    }

    [Fact]
    public async Task ACurrentIndex_IsKeptAcrossOpens()
    {
        await using (WorkspaceIndexContext first = await OpenAsync())
        {
            _ = await first.Database.ExecuteSqlRawAsync("INSERT INTO \"Folders\" (\"RelativePath\", \"Description\", \"LastModified\", \"UpdatedAt\") VALUES ('keep', '', 0, 0);");
        }

        await using WorkspaceIndexContext again = await OpenAsync();

        Assert.Equal(1, await ScalarAsync(again, "SELECT COUNT(*) FROM \"Folders\";"));
    }

    [Theory]
    [InlineData("F:\\repo", "F:\\repo\\")]
    [InlineData("F:\\repo\\", "F:\\repo")]
    [InlineData("F:\\repo", "F:\\repo\\\\")]
    public void TwoSpellingsOfOneRoot_ShareOneKey(string a, string b)
    {
        // Relative and trailing-separator spellings must normalize to the same cache key.
        Assert.Equal(WorkspaceIndexContextFactory.NormalizeRoot(a), WorkspaceIndexContextFactory.NormalizeRoot(b));
    }

    [Fact]
    public async Task TheAccessor_OpensOneContextForTwoSpellingsOfTheRoot()
    {
        using WorkspaceIndexDbAccessor accessor = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspaceIndexDbAccessor>.Instance);

        WorkspaceIndexContext a = await accessor.GetDbAsync(_root, CancellationToken.None);
        WorkspaceIndexContext b = await accessor.GetDbAsync(_root + Path.DirectorySeparatorChar, CancellationToken.None);

        Assert.Same(a, b);
    }
}
