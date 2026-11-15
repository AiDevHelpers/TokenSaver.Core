using System.Text;
using TokenSaver.LogHelper.Models;
using TokenSaver.LogHelper.Sources;
using Xunit;

namespace TokenSaver.LogHelper.Tests.Sources;

public sealed class FileRotationTests : IDisposable
{
    private readonly string _root;
    private readonly LogHelperOptions _options;

    public FileRotationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tsl-rot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _options = new LogHelperOptions { AllowedRoots = new[] { _root } };
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string PathFor(string name) => Path.Combine(_root, name);

    private FileLogSource Open(string path) => new(path, _options);

    [Fact]
    public async Task RenameRotation_NewFileStartsFromZero()
    {
        string path = PathFor("app.log");
        File.WriteAllText(path, "[INF] before rotation\n", new UTF8Encoding(false));
        var first = await Open(path).ReadAsync(null, 100, CancellationToken.None);

        File.Move(path, PathFor("app.1.log"));
        File.WriteAllText(path, "[INF] after rotation\n", new UTF8Encoding(false));

        var second = await Open(path).ReadAsync(first.NextCursor, 100, CancellationToken.None);

        Assert.True(second.Reset);
        Assert.Equal(new[] { "after rotation" }, second.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task CopyTruncate_LongerRewrite_DetectedByAnchor()
    {
        string path = PathFor("copy.log");
        File.WriteAllText(path, "[INF] one line\n", new UTF8Encoding(false));
        var first = await Open(path).ReadAsync(null, 100, CancellationToken.None);

        File.WriteAllText(path, "[INF] a much longer replacement line that passes the old offset\n", new UTF8Encoding(false));
        var second = await Open(path).ReadAsync(first.NextCursor, 100, CancellationToken.None);

        Assert.True(second.Reset);
        Assert.Single(second.Entries);
    }

    [Fact]
    public async Task DeletedWhileOpen_NoThrow_ReturnsEmpty()
    {
        string path = PathFor("gone.log");
        File.WriteAllText(path, "[INF] here\n", new UTF8Encoding(false));
        var first = await Open(path).ReadAsync(null, 100, CancellationToken.None);

        File.Delete(path);
        var second = await Open(path).ReadAsync(first.NextCursor, 100, CancellationToken.None);

        Assert.Empty(second.Entries);
    }
}
