namespace AiDevTools.TokenSaver.TokenEconomy.Models.Context;

public class ConversationIndex
{
    public IReadOnlyList<string> RequestedFiles { get; set; } = [];
    public IReadOnlyList<string> NeededItems { get; set; } = [];
    public IReadOnlyList<string> ClarityNotes { get; set; } = [];
    public long Updated { get; set; }
}
