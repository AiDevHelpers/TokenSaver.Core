using TokenSaver.TokenEconomy.Models.TokenEconomy;
using TokenSaver.TokenEconomy.Services;

using Xunit;

namespace TokenSaver.TokenEconomy.Tests;

/// <summary>
///     Covers IChunkExtractor's regex-boundary heuristics directly — pure string-in/chunks-out,
///     no Ollama/DB dependency, so every case here is fast and fully deterministic.
/// </summary>
public class ChunkExtractorTests
{
    private readonly ChunkExtractor _extractor = new();

    [Fact]
    public void UnsupportedExtension_ReturnsWholeFileModuleChunk()
    {
        IReadOnlyList<CodeChunk> chunks = _extractor.ExtractChunks("notes.txt", "line one\nline two\nline three");

        CodeChunk chunk = Assert.Single(chunks);
        Assert.Equal("module", chunk.ChunkType);
        Assert.Equal("notes.txt", chunk.Name);
        Assert.Equal(1, chunk.StartLine);
        Assert.Equal(3, chunk.EndLine);
    }

    [Fact]
    public void CSharpFile_NoBoundaries_FallsBackToModuleChunk()
    {
        // A .cs file whose only content is comments/usings — no class/method boundary lines.
        const string content = "// just a comment\nusing System;\n";

        IReadOnlyList<CodeChunk> chunks = _extractor.ExtractChunks("Empty.cs", content);

        CodeChunk chunk = Assert.Single(chunks);
        Assert.Equal("module", chunk.ChunkType);
    }

    [Fact]
    public void CSharpFile_ClassDeclaration_IsDetectedAsBoundary()
    {
        const string content =
            "namespace Foo;\n" +
            "\n" +
            "public sealed class Widget\n" +
            "{\n" +
            "    public void DoThing() { }\n" +
            "}\n";

        IReadOnlyList<CodeChunk> chunks = _extractor.ExtractChunks("Widget.cs", content);

        Assert.Contains(chunks, c => c.Name == "Widget" && c.ChunkType == "function");
    }

    [Fact]
    public void MarkdownFile_NoHeadings_ReturnsSingleSectionCoveringWholeFile()
    {
        const string content = "just some prose\nwith no headings at all";

        IReadOnlyList<CodeChunk> chunks = _extractor.ExtractChunks("README.md", content);

        CodeChunk chunk = Assert.Single(chunks);
        Assert.Equal("md_section", chunk.ChunkType);
        Assert.Equal("(no headings)", chunk.Name);
    }

    [Fact]
    public void MarkdownFile_NestedHeadings_SplitsOnEqualOrHigherLevelOnly()
    {
        const string content =
            "# Title\n" +
            "intro text\n" +
            "## Section A\n" +
            "content A\n" +
            "### Subsection A.1\n" +
            "content A.1\n" +
            "## Section B\n" +
            "content B\n";

        IReadOnlyList<CodeChunk> chunks = _extractor.ExtractChunks("doc.md", content);

        // "Section A" should run through "Subsection A.1" (a deeper heading doesn't end it) and
        // stop right before "Section B" (an equal-level heading does end it).
        CodeChunk sectionA = Assert.Single(chunks, c => c.Name == "Section A");
        Assert.Contains("Subsection A.1", sectionA.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Section B", sectionA.Text, StringComparison.Ordinal);
    }
}
