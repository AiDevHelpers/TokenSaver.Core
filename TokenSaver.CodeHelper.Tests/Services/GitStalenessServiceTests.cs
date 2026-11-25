using AiDevTools.TokenSaver.CodeHelper.Indexing;
using AiDevTools.TokenSaver.Database.Context;
using AiDevTools.TokenSaver.Database.Context.Entities;
using AiDevTools.TokenSaver.Utilities.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using System.Diagnostics;

using Xunit;

namespace AiDevTools.TokenSaver.CodeHelper.Tests;

public sealed class GitStalenessServiceTests : IDisposable
{
    private readonly string _root;
    private readonly WorkspaceIndexDbAccessor _accessor;
    private readonly GitStalenessService _service;

    public GitStalenessServiceTests()
    {
        _root = Directory.CreateTempSubdirectory("tsgs_").FullName;
        _accessor = new WorkspaceIndexDbAccessor(NullLogger<WorkspaceIndexDbAccessor>.Instance);
        _service = new GitStalenessService(
            _accessor,
            new GitStalenessChecker(NullLogger<GitStalenessChecker>.Instance),
            NullLogger<GitStalenessService>.Instance);
    }

    public void Dispose()
    {
        _accessor.Dispose();
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void EnumerateAllFiles_ReturnsFilesUnderTree_ExcludingSkippedDirs()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        string sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "b.txt"), "b");

        string binDir = Path.Combine(_root, "bin");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "should-be-skipped.txt"), "x");

        List<string> files = GitStalenessService.EnumerateAllFiles(_root, _root).ToList();

        Assert.Contains(files, f => f.EndsWith("a.txt", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("sub/b.txt", StringComparison.Ordinal) || f.EndsWith("sub\\b.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => f.Contains("should-be-skipped.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void EnumerateAllFiles_DoesNotYieldGitInternals_ForRealRepo()
    {
        RunGit(_root, "init");
        File.WriteAllText(Path.Combine(_root, "tracked.txt"), "content");

        List<string> files = GitStalenessService.EnumerateAllFiles(_root, _root).ToList();

        Assert.DoesNotContain(files, f => f.Contains(".git/", StringComparison.Ordinal) || f.Contains(".git\\", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("tracked.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void EnumerateAllFiles_RespectsGitignore()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "ignored.txt\n");
        File.WriteAllText(Path.Combine(_root, "ignored.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "kept.txt"), "y");

        List<string> files = GitStalenessService.EnumerateAllFiles(_root, _root).ToList();

        Assert.DoesNotContain(files, f => f.EndsWith("ignored.txt", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("kept.txt", StringComparison.Ordinal));
    }

    private static async Task<int> EnsureRootFolderAsync(WorkspaceIndexContext ctx)
    {
        FolderEntity folder = new() { RelativePath = ".", ParentFolderId = null, Description = "", LastModified = 0, UpdatedAt = 0 };
        _ = ctx.Folders.Add(folder);
        _ = await ctx.SaveChangesAsync();
        return folder.Id;
    }

    [Fact]
    public async Task ResolveTargetFilesAsync_ReturnsNull_WhenNoGitRepoAnywhere()
    {
        WorkspaceIndexContext ctx = await _accessor.GetDbAsync(_root, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "a");

        List<string>? targets = await _service.ResolveTargetFilesAsync(ctx, _root, CancellationToken.None);

        Assert.Null(targets);
    }

    [Fact]
    public async Task ResolveTargetFilesAsync_FirstRunOnRealRepo_ReturnsFullFileList()
    {
        RunGit(_root, "init");
        RunGit(_root, "config user.email test@example.com");
        RunGit(_root, "config user.name Test");
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(_root, "b.txt"), "b");
        RunGit(_root, "add -A");
        RunGit(_root, "commit -m init");

        WorkspaceIndexContext ctx = await _accessor.GetDbAsync(_root, CancellationToken.None);
        List<string>? targets = await _service.ResolveTargetFilesAsync(ctx, _root, CancellationToken.None);

        Assert.NotNull(targets);
        Assert.Contains("a.txt", targets);
        Assert.Contains("b.txt", targets);
    }

    [Fact]
    public async Task ResolveTargetFilesAsync_SecondRunUnchanged_ReturnsEmptyList()
    {
        if (!GitAvailable())
        {
            return;
        }

        RunGit(_root, "init");
        RunGit(_root, "config user.email test@example.com");
        RunGit(_root, "config user.name Test");
        await File.WriteAllTextAsync(Path.Combine(_root, ".gitignore"), ".tokensaver/\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "a");
        RunGit(_root, "add -A");
        RunGit(_root, "commit -m init");

        WorkspaceIndexContext ctx = await _accessor.GetDbAsync(_root, CancellationToken.None);
        _ = await _service.ResolveTargetFilesAsync(ctx, _root, CancellationToken.None);

        List<string>? second = await _service.ResolveTargetFilesAsync(ctx, _root, CancellationToken.None);

        Assert.NotNull(second);
        Assert.Empty(second);
    }

    private static bool GitAvailable()
    {
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo("git", "--version")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(5000);
            return p is { ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }

    private static void RunGit(string workingDir, string arguments)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        _ = process.Start();
        process.WaitForExit(10_000);
    }
}
