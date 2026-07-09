using TokenSaver.ContextHelper.Models.Diagnostics;

namespace TokenSaver.ContextHelper.Models;

public class PipelineResult
{
    public string ContextText { get; set; } = "";
    public int TokenCount { get; set; }
    public bool Alert { get; set; }
    public string? AlertMessage { get; set; }
    public SpaghettiReport? SpaghettiWarning { get; set; }
    public string? OverStrippingWarning { get; set; }
    public PipelineSummary Summary { get; set; } = new();
}
