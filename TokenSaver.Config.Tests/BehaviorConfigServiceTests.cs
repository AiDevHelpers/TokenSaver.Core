using TokenSaver.Config;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace TokenSaver.Config.Tests;

public sealed class BehaviorConfigServiceTests : IDisposable
{
    private readonly string _root;
    private readonly BehaviorConfigService _service;

    public BehaviorConfigServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ts-cfg-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(Path.Combine(_root, ".tokensaver"));
        _service = new BehaviorConfigService(NullLogger<BehaviorConfigService>.Instance);
    }

    [Fact]
    public void GetConfig_NoFiles_ReturnsUnset()
    {
        TokenSaverBehaviorConfig cfg = _service.GetConfig(_root);
        Assert.Null(cfg.AddTokenSaverToGitignore);
    }

    [Fact]
    public void SetLocal_ThenGet_ReflectsValue()
    {
        _service.Set(_root, ConfigScope.Local, c => c with { AddTokenSaverToGitignore = true });

        Assert.True(_service.GetConfig(_root).AddTokenSaverToGitignore);
        Assert.True(File.Exists(BehaviorConfigService.LocalConfigPath(_root)));
    }

    [Fact]
    public void LocalFile_PresentValue_IsHonored()
    {
        // Write a local file directly; global is left to the machine default and may or may not exist,
        // so this asserts only that a present local value is honored.
        File.WriteAllText(
            BehaviorConfigService.LocalConfigPath(_root),
            "{ \"addTokenSaverToGitignore\": false }");

        Assert.False(_service.GetConfig(_root).AddTokenSaverToGitignore);
    }

    [Fact]
    public void MalformedJson_IsIgnored_NotThrown()
    {
        File.WriteAllText(BehaviorConfigService.LocalConfigPath(_root), "{ not valid json ");

        TokenSaverBehaviorConfig cfg = _service.GetConfig(_root);
        Assert.Null(cfg.AddTokenSaverToGitignore);
    }

    public void Dispose()
    {
        _service.Dispose();
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
