using TokenSaver.CodeHelper.Indexing;
using TokenSaver.CodeHelper.Models.Index;

using Xunit;

namespace TokenSaver.CodeHelper.Tests.Indexing;

public sealed class CSharpSymbolParserReproTests
{
    private const string EventPullServiceSource = """
        using Microsoft.Extensions.Hosting;

        namespace VideoForensics.Hosting.Services;

        /// <summary>Pulls events in the background.</summary>
        public sealed class EventPullService : BackgroundService
        {
            private readonly ILogger<EventPullService> _logger;

            public EventPullService(ILogger<EventPullService> logger)
            {
                _logger = logger;
            }

            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                await Task.CompletedTask;
            }
        }
        """;

    [Fact]
    public void Parse_CSharpFile_ReturnsClassSymbol()
    {
        var parser = new CSharpSymbolParser();

        List<SymbolEntry> symbols = parser.Parse(
            "src/client/host/VideoForensics.Hosting/src/Services/EventPullService.cs",
            EventPullServiceSource).ToList();

        SymbolEntry? cls = symbols.FirstOrDefault(s => s.Name == "EventPullService" && s.Kind == SymbolKind.Class);
        Assert.NotNull(cls);
        Assert.Equal(6, cls!.Line);
    }

    [Fact]
    public void Parse_SameNameTypeAndPropertyOnOneLine_ProducesDistinctIds()
    {
        // The property regex also matches "public interface IFoo {" on one line, so the interface
        // and a Property share name and line. Their ids must still differ or the insert collides.
        const string source = """
            namespace N
            {
                public interface IFoo { }
            }
            """;
        var parser = new CSharpSymbolParser();

        List<SymbolEntry> symbols = parser.Parse("Foo.cs", source).ToList();

        Assert.Contains(symbols, s => s.Name == "IFoo" && s.Kind == SymbolKind.Interface);
        Assert.Contains(symbols, s => s.Name == "IFoo" && s.Kind == SymbolKind.Property);
        Assert.Equal(symbols.Count, symbols.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void Parse_ReadonlyRecordStruct_ReturnsNamedType()
    {
        const string source = """
            namespace N;

            public readonly record struct LogRecord(int Seq, string Text);
            public record struct Point(int X, int Y);
            public sealed record Person(string Name);
            """;
        var parser = new CSharpSymbolParser();

        List<SymbolEntry> symbols = parser.Parse("Records.cs", source).ToList();

        Assert.Contains(symbols, s => s.Name == "LogRecord" && s.Kind == SymbolKind.Class);
        Assert.Contains(symbols, s => s.Name == "Point" && s.Kind == SymbolKind.Class);
        Assert.Contains(symbols, s => s.Name == "Person" && s.Kind == SymbolKind.Class);
        Assert.DoesNotContain(symbols, s => s.Name == "struct");
    }

    [Fact]
    public void Parse_CSharpFile_ReturnsMethodsWithContainingType()
    {
        var parser = new CSharpSymbolParser();

        List<SymbolEntry> symbols = parser.Parse("EventPullService.cs", EventPullServiceSource).ToList();

        SymbolEntry? method = symbols.FirstOrDefault(s => s.Name == "ExecuteAsync");
        Assert.NotNull(method);
        Assert.Equal("EventPullService", method!.ContainingType);
    }
}
