using TokenSaver.TokenEconomy.Services;

using Xunit;

namespace TokenSaver.TokenEconomy.Tests;

public sealed class CleanSummaryTests
{
    [Fact]
    public void PlainSentence_IsUnchanged()
    {
        Assert.Equal("Hashes a string.", RepoIndexer.CleanSummary("Hashes a string."));
    }

    [Fact]
    public void PreambleLineEndingInColon_IsDropped()
    {
        string raw = "Here's the revised code with a one-liner:\n\nComputes a SHA-256 hash of the text.\n\nExplanation of changes:\n- x";

        Assert.Equal("Computes a SHA-256 hash of the text.", RepoIndexer.CleanSummary(raw));
    }

    [Fact]
    public void FencedCodeBlock_IsRemoved()
    {
        string raw = "```csharp\nint x = 1;\n```\nSets x to one.";

        Assert.Equal("Sets x to one.", RepoIndexer.CleanSummary(raw));
    }

    [Fact]
    public void LongLine_IsCappedAt300()
    {
        string cleaned = RepoIndexer.CleanSummary(new string('a', 900));

        Assert.True(cleaned.Length <= 300);
    }

    [Fact]
    public void OnlyCodeFence_FallsBackToTrimmedRaw()
    {
        Assert.Equal("```x```", RepoIndexer.CleanSummary("  ```x```  "));
    }
}
