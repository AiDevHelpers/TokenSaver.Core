#pragma warning disable CA2000 // Test factories hand disposables to the code under test; each test is short-lived.
using System.Net;
using System.Net.Http;
using System.Text;

using TokenSaver.Common.Core.Configuration;
using TokenSaver.OllamaIntegration.Services;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace TokenSaver.OllamaIntegration.Tests;

public sealed class OllamaGenerateReplyTests
{
    private const string WarmingUpBody = "{\"status\":\"warming_up\",\"message\":\"Models warming up.\",\"retry_after\":1}";

    private static OllamaService Build(HttpMessageHandler handler)
    {
        HttpClient http = new(handler)
        {
            BaseAddress = new Uri("http://localhost:11435")
        };
        return new OllamaService(
            http,
            Options.Create(new TokenSaverConfig()),
            NullLogger<OllamaService>.Instance);
    }

    private static OllamaService Build(string responseBody) => Build(new StaticHandler(responseBody));

    [Fact]
    public async Task Generate_PersistentWarmup_ThrowsAfterRetriesInsteadOfEmptyString()
    {
        OllamaService svc = Build(WarmingUpBody);

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => svc.CallModelAsync("hi", CancellationToken.None));

        Assert.Contains("warming_up", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_WarmupThenResponse_RetriesAndReturnsText()
    {
        var handler = new SequenceHandler(WarmingUpBody, "{\"response\":\"after warmup\"}");
        OllamaService svc = Build(handler);

        string reply = await svc.CallModelAsync("hi", CancellationToken.None);

        Assert.Equal("after warmup", reply);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Generate_ReplyWithEmptyResponse_ReturnsEmptyString()
    {
        OllamaService svc = Build("{\"response\":\"\"}");

        string reply = await svc.CallModelAsync("hi", CancellationToken.None);

        Assert.Equal("", reply);
    }

    [Fact]
    public async Task Generate_ReplyWithResponse_ReturnsText()
    {
        OllamaService svc = Build("{\"response\":\"chat test ok\"}");

        string reply = await svc.CallModelAsync("hi", CancellationToken.None);

        Assert.Equal("chat test ok", reply);
    }

    private sealed class StaticHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>Returns the given bodies in order; the last one repeats once the list is exhausted.</summary>
    private sealed class SequenceHandler(params string[] bodies) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = bodies[Math.Min(Calls, bodies.Length - 1)];
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
