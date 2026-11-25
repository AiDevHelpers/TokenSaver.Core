using AiDevTools.TokenSaver.Common.Contracts;
using AiDevTools.TokenSaver.Utilities.Services;

using Microsoft.Extensions.Logging;

using System.Text;

namespace AiDevTools.TokenSaver.CodeHelper.Indexing;

/// <summary>
///     Produces a per-file description: AI summary (when a summarizer is configured and the
///     extension is a supported source-code type) with a static-extraction fallback.
/// </summary>
public sealed partial class AiSummarizationService
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "AI summary failed for {File}, falling back to static extraction")]
    private static partial void LogAiSummaryFailed(ILogger logger, Exception ex, string file);

    private static readonly HashSet<string> AiSummaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".rs", ".cpp", ".c", ".h",
        ".fs", ".vb", ".swift", ".kt", ".java"
    };

    private readonly ILogger<AiSummarizationService> _logger;
    private readonly ICodeSummarizer? _summarizer;

    public AiSummarizationService(ILogger<AiSummarizationService> logger, ICodeSummarizer? summarizer = null)
    {
        _logger = logger;
        _summarizer = summarizer;
    }

    public async Task<string> GetFileDescriptionAsync(string absPath, string relPath, string ext,
        SemaphoreSlim aiThrottle, bool preferBackupEndpoint, CancellationToken ct)
    {
        if (_summarizer != null && AiSummaryExtensions.Contains(ext))
        {
            try
            {
                const int maxInput = 4096;
                await using FileStream fs = new(absPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] buf = new byte[maxInput];
                int read = await fs.ReadAsync(buf.AsMemory(0, maxInput), ct);
                string text = Encoding.UTF8.GetString(buf, 0, read);
                string hash = WorkspaceIndexDbAccessor.ComputeHash(text);

                await aiThrottle.WaitAsync(ct);
                try
                {
                    string? summary = await _summarizer.SummarizeAsync(
                        text, Path.GetFileName(absPath), hash, preferBackupEndpoint, ct);
                    if (!string.IsNullOrWhiteSpace(summary))
                    {
                        return summary;
                    }
                }
                finally
                {
                    _ = aiThrottle.Release();
                }
            }
            catch (Exception ex)
            {
                LogAiSummaryFailed(_logger, ex, relPath);
            }
        }

        return await StaticFileSummarizer.ExtractAsync(absPath) ?? Path.GetFileName(relPath);
    }
}
