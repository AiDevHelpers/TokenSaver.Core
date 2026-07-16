using TokenSaver.Config;

using Xunit;

namespace TokenSaver.Config.Tests;

public sealed class WorkspaceIgnoreFilterTests : IDisposable
{
    private readonly string _root;

    public WorkspaceIgnoreFilterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ts-ignore-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(Path.Combine(_root, ".tokensaver"));
    }

    private void WriteGitignore(string content) => File.WriteAllText(Path.Combine(_root, ".gitignore"), content);
    private void WriteAdditional(string content) => File.WriteAllText(WorkspaceIgnoreFilter.AdditionalPath(_root), content);
    private void WriteBypass(string content) => File.WriteAllText(WorkspaceIgnoreFilter.BypassPath(_root), content);

    [Fact]
    public void GitignoreAlone_StillHonored()
    {
        WriteGitignore("bin/\n*.log\n");
        WorkspaceIgnoreFilter f = WorkspaceIgnoreFilter.Load(_root);

        Assert.True(f.IsIgnored("bin", true));
        Assert.True(f.IsIgnored("app.log", false));
        Assert.False(f.IsIgnored("src/main.cs", false));
    }

    [Fact]
    public void IgnoreAdditional_ExpandsExclusions_BeyondGitignore()
    {
        WriteGitignore("bin/\n");
        WriteAdditional("scratch/\n*.tmp\n");
        WorkspaceIgnoreFilter f = WorkspaceIgnoreFilter.Load(_root);

        Assert.True(f.IsIgnored("scratch", true));
        Assert.True(f.IsIgnored("notes.tmp", false));
        Assert.True(f.IsIgnored("bin", true)); // gitignore still applies
    }

    [Fact]
    public void IgnoreBypass_ForcesInclusion_OfGitignoredPath()
    {
        WriteGitignore("dist/\n");
        WriteBypass("dist/\n");
        WorkspaceIgnoreFilter f = WorkspaceIgnoreFilter.Load(_root);

        // dist is gitignored but bypass wins → included.
        Assert.False(f.IsIgnored("dist", true));
    }

    [Fact]
    public void IgnoreBypass_OverridesIgnoreAdditional()
    {
        WriteAdditional("secrets/\n");
        WriteBypass("secrets/public/\n");
        WorkspaceIgnoreFilter f = WorkspaceIgnoreFilter.Load(_root);

        Assert.True(f.IsIgnored("secrets", true));
        Assert.False(f.IsIgnored("secrets/public", true));
    }

    [Fact]
    public void Append_DeDupes_AndCreatesFile()
    {
        string path = WorkspaceIgnoreFilter.AdditionalPath(_root);
        IReadOnlyList<string> first = WorkspaceIgnoreFilter.Append(path, ["foo/", "bar/"]);
        IReadOnlyList<string> second = WorkspaceIgnoreFilter.Append(path, ["bar/", "baz/"]);

        Assert.Equal(2, first.Count);
        Assert.Equal(["baz/"], second);
        string[] lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToArray();
        Assert.Equal(["foo/", "bar/", "baz/"], lines);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            /* best-effort temp cleanup */
        }
    }
}
