using AiDevTools.TokenSaver.Common.Core.Configuration;

using Microsoft.Extensions.Options;

namespace AiDevTools.TokenSaver.Utilities.Services;

/// <summary>
///     Escape hatch for distilling tools: writes raw output to
///     "&lt;WorkspaceRoot&gt;/.tokensaver/raw/&lt;tool&gt;/&lt;timestamp&gt;.txt" before
///     distillation, so a compressed summary is never a dead end — Claude (or the user) can read
///     the full raw artifact by path if the summary isn't enough. Gated by
///     TokenEconomyConfig.EnableRawRetention; <see cref="WriteAsync" /> returns null (skips the
///     write) when disabled, and callers should omit the "full: &lt;path&gt;" reference from
///     their response text in that case rather than reference a path that was never written.
/// </summary>
public sealed class RawArtifactWriter(IOptions<TokenSaverConfig> config)
{
    private readonly TokenSaverConfig _config = config.Value;

    public async Task<string?> WriteAsync(string tool, string raw, CancellationToken ct = default)
    {
        if (!_config.TokenEconomy.EnableRawRetention)
        {
            return null;
        }

        string root = string.IsNullOrWhiteSpace(_config.WorkspaceRoot) ? Directory.GetCurrentDirectory() : _config.WorkspaceRoot;
        string dir = Path.Combine(root, ".tokensaver", "raw", tool);
        _ = Directory.CreateDirectory(dir);

        string path = Path.Combine(dir, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.txt");
        await File.WriteAllTextAsync(path, raw, ct);
        return path;
    }
}
