namespace TokenSaver.Library.Models.Ollama;

public class GpuStats
{
    public string Gpu { get; set; } = "unavailable";
    public int? VramUsedMb { get; set; }
    public int? VramTotalMb { get; set; }
    public int? GpuUtilPct { get; set; }
}
