using TokenSaver.LogHelper.Contracts;
using TokenSaver.LogHelper.Models;

namespace TokenSaver.LogHelper.Sources;

/// <summary>
/// Serves <c>file:</c> channels. <see cref="ListChannelsAsync"/> returns nothing for now; channel
/// enumeration arrives with the LogChannels tool.
/// </summary>
public sealed class FileLogSourceProvider : ILogSourceProvider
{
    private readonly LogHelperOptions _options;

    public FileLogSourceProvider(LogHelperOptions options)
    {
        _options = options;
    }

    public string Scheme => "file";

    public Task<IReadOnlyList<LogChannelInfo>> ListChannelsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LogChannelInfo>>(Array.Empty<LogChannelInfo>());

    public ILogSource Open(ChannelId id) => new FileLogSource(id.Name, _options);
}
