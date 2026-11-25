namespace AiDevTools.TokenSaver.CodeHelper.Models.Index;

public enum SymbolKind
{
    Class,
    Interface,
    Struct,
    Enum,
    Delegate,
    Method,
    Constructor,
    Property,
    Field,
    Event,
    Namespace,
    Variable,
    Parameter,
    TypeAlias,
    Function, // TypeScript / Python free functions
    Unknown
}
