namespace TokenSaver.Library.Models.Context;

public class ContextHygieneReport
{
    public int FileCount { get; set; }
    public int SectionCount { get; set; }
    public IReadOnlyList<StaleSection> StaleSections { get; set; } = [];
    public ConversationIndex? ConversationIndex { get; set; }
}
