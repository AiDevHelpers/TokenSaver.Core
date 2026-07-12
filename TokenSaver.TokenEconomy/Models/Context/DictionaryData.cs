namespace TokenSaver.TokenEconomy.Models.Context;

public class DictionaryData
{
    public long Created { get; set; }
    public Dictionary<string, string> Mappings { get; set; } = [];
    public IReadOnlyList<string> CommonWords { get; set; } = [];
}
