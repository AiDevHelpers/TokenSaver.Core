using System;
using System.IO;
using TokenSaver.Client.Helpers.Configuration;
using Xunit;

namespace TokenSaver.Client.Helpers.Tests.Configuration;

public sealed class GitignoreHelperTests
{
    [Fact]
    public void IsTokenSaverInGitignore_WithNoGitignore_ReturnsFalse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var result = GitignoreHelper.IsTokenSaverInGitignore(tempDir);
            Assert.False(result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void IsTokenSaverInGitignore_WithTokenSaverEntry_ReturnsTrue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var gitignorePath = Path.Combine(tempDir, ".gitignore");
            File.WriteAllText(gitignorePath, ".tokensaver/\n");

            var result = GitignoreHelper.IsTokenSaverInGitignore(tempDir);
            Assert.True(result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void AddTokenSaverToGitignore_CreatesGitignore_WhenNotPresent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            GitignoreHelper.AddTokenSaverToGitignore(tempDir);

            var gitignorePath = Path.Combine(tempDir, ".gitignore");
            Assert.True(File.Exists(gitignorePath));

            var content = File.ReadAllText(gitignorePath);
            Assert.Contains(".tokensaver/", content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
