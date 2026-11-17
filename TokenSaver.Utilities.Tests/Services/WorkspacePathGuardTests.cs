using TokenSaver.Utilities.Services;

using Xunit;

namespace TokenSaver.Utilities.Tests;

public sealed class WorkspacePathGuardTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "tsg-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _outside;

    public WorkspacePathGuardTests()
    {
        _root = Path.Combine(_base, "repo");
        _outside = Path.Combine(_base, "outside");
        _ = Directory.CreateDirectory(Path.Combine(_root, "src"));
        _ = Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_root, "src", "a.cs"), "x");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "s");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("src/a.cs")]
    [InlineData("src\\a.cs")]
    [InlineData("./src/../src/a.cs")]
    [InlineData("new-file-not-yet-created.cs")]
    public void RelativePathsInsideTheRoot_AreAllowed(string path)
    {
        PathCheck check = WorkspacePathGuard.Resolve(path, _root);
        Assert.True(check.Ok, check.Error);
        Assert.StartsWith(_root, check.FullPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AbsolutePathInsideTheRoot_IsAllowed()
    {
        Assert.True(WorkspacePathGuard.Resolve(Path.Combine(_root, "src", "a.cs"), _root).Ok);
    }

    [Theory]
    [InlineData("../outside/secret.txt")]
    [InlineData("src/../../outside/secret.txt")]
    [InlineData("..")]
    public void DotDotEscapes_AreRefused(string path)
    {
        PathCheck check = WorkspacePathGuard.Resolve(path, _root);
        Assert.False(check.Ok);
        Assert.Contains("outside the workspace root", check.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AbsolutePathOutsideTheRoot_IsRefused()
    {
        Assert.False(WorkspacePathGuard.Resolve(Path.Combine(_outside, "secret.txt"), _root).Ok);
    }

    [Fact]
    public void SiblingFolderSharingTheRootPrefix_IsRefused()
    {
        string sibling = _root + "-evil";
        _ = Directory.CreateDirectory(sibling);
        Assert.False(WorkspacePathGuard.Resolve(Path.Combine(sibling, "x.txt"), _root).Ok);
    }

    [Fact]
    public void UncPath_IsRefused()
    {
        string unc = OperatingSystem.IsWindows() ? @"\\server\share\x.txt" : "//server/share/x.txt";
        Assert.False(WorkspacePathGuard.Resolve(unc, _root).Ok);
    }

    [Fact]
    public void EmptyRootOrPath_IsRefusedWithAClearMessage()
    {
        PathCheck noRoot = WorkspacePathGuard.Resolve("a.cs", "");
        Assert.False(noRoot.Ok);
        Assert.Contains("No workspace root", noRoot.Error, StringComparison.Ordinal);

        PathCheck noPath = WorkspacePathGuard.Resolve("", _root);
        Assert.False(noPath.Ok);
        Assert.Contains("path is required", noPath.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtraRoot_IsAllowed()
    {
        string secret = Path.Combine(_outside, "secret.txt");
        Assert.False(WorkspacePathGuard.Resolve(secret, _root).Ok);
        Assert.True(WorkspacePathGuard.Resolve(secret, _root, _outside).Ok);
    }

    [Fact]
    public void DirectoryLinkPointingOutside_IsRefused()
    {
        if (!TryLink(Path.Combine(_root, "escape"), _outside))
        {
            return; // Symlink creation needs privileges on some machines; nothing to assert there.
        }

        PathCheck check = WorkspacePathGuard.Resolve("escape/secret.txt", _root);
        Assert.False(check.Ok);
        Assert.Contains("outside the workspace root", check.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectoryLinkInsideTheRoot_IsAllowed()
    {
        if (!TryLink(Path.Combine(_root, "alias"), Path.Combine(_root, "src")))
        {
            return;
        }

        PathCheck check = WorkspacePathGuard.Resolve("alias/a.cs", _root);
        Assert.True(check.Ok, check.Error);
    }

    private static bool TryLink(string link, string target)
    {
        try
        {
            _ = Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
