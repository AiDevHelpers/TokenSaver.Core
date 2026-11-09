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
    private static OllamaService Build(string responseBody)
    {
        HttpClient http = new(new StaticHandler(responseBody))
        {
            BaseAddress = new Uri("http://localhost:11435")
        };
        return new OllamaService(
            http,
            Options.Create(new TokenSaverConfig()),
            NullLogger<OllamaService>.Instance);
    }

    [Fact]
    public async Task Generate_ReplyWithoutResponseField_ThrowsInsteadOfEmptyString()
    {
        OllamaService svc = Build("{\"status\":\"warming_up\",\"message\":\"Models warming up.\",\"retry_after\":3}");

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => svc.CallModelAsync("hi", CancellationToken.None));

        Assert.Contains("warming_up", ex.Message, StringComparison.Ordinal);
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
}
