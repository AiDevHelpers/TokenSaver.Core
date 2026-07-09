namespace TokenSaver.ContextHelper.Models.Diagnostics;

public class SessionSignals
{
    public int GetCodeBlockCalls { get; set; }
    public int RebuildContextCalls { get; set; }
    public int TurnsWithFileRequests { get; set; }
    public double EstimatedWastedTokens { get; set; }
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;
}
