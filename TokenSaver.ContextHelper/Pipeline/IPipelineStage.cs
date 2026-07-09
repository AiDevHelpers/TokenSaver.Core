namespace TokenSaver.ContextHelper.Pipeline;

public interface IPipelineStage
{
    string Name { get; }
    Task<PipelineContext> ProcessAsync(PipelineContext ctx, CancellationToken ct = default);
}
