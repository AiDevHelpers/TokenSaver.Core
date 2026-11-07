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
    public void Parse_CSharpFile_ReturnsMethodsWithContainingType()
    {
        var parser = new CSharpSymbolParser();

        List<SymbolEntry> symbols = parser.Parse("EventPullService.cs", EventPullServiceSource).ToList();

        SymbolEntry? method = symbols.FirstOrDefault(s => s.Name == "ExecuteAsync");
        Assert.NotNull(method);
        Assert.Equal("EventPullService", method!.ContainingType);
    }
}
