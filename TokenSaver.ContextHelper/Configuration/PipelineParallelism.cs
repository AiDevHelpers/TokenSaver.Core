namespace TokenSaver.ContextHelper.Configuration;

/// <summary>
///     Shared parallelism budget: all CPU threads minus 2 (reserved for OS + ASP.NET I/O pump).
///     On a 4-core machine this yields degree 2; on 16 cores it yields 14.
/// </summary>
public static class PipelineParallelism
{
    public static readonly int Degree = Math.Max(1, Environment.ProcessorCount - 2);

    /// <summary>
    ///     Shared semaphore for throttling <c>Task.WhenAll</c> fan-outs (e.g. embedding calls)
    ///     that must not saturate Ollama beyond the available thread budget.
    /// </summary>
    public static readonly SemaphoreSlim Semaphore = new(Degree, Degree);

    /// <summary>New <see cref="ParallelOptions" /> capped at <see cref="Degree" />.</summary>
    public static ParallelOptions Options => new() { MaxDegreeOfParallelism = Degree };
}
