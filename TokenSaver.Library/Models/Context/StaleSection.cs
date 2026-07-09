namespace TokenSaver.Library.Models.Context;

public class StaleSection
{
    public int Id { get; set; }
    public string RelativePath { get; set; } = "";
    public string? SectionName { get; set; }
}
