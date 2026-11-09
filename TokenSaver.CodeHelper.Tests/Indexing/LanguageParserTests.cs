using TokenSaver.CodeHelper.Indexing;
using TokenSaver.CodeHelper.Models.Index;

using Xunit;

namespace TokenSaver.CodeHelper.Tests.Indexing;

public sealed class LanguageParserTests
{
    [Fact]
    public void CSharp_GenericClassEnumAndDelegate_ReturnsNamedTypes()
    {
        const string source = """
            namespace N;

            public sealed class Repo<T> where T : class { }
            public enum Status { Ok, Failed }
            public delegate void Handler(int x);
            """;

        List<SymbolEntry> symbols = new CSharpSymbolParser().Parse("Types.cs", source).ToList();

        Assert.Contains(symbols, s => s.Name == "Repo" && s.Kind == SymbolKind.Class);
        Assert.Contains(symbols, s => s.Name == "Status" && s.Kind == SymbolKind.Enum);
        Assert.Contains(symbols, s => s.Name == "Handler" && s.Kind == SymbolKind.Delegate);
    }

    [Fact]
    public void CSharp_TypeLineNumbers_MatchDeclarationLines()
    {
        const string source = """
            namespace N
            {
                public class First
                {
                }

                public class Second
                {
                }
            }
            """;

        List<SymbolEntry> symbols = new CSharpSymbolParser().Parse("Lines.cs", source).ToList();

        Assert.Equal(3, symbols.Single(s => s.Name == "First").Line);
        Assert.Equal(7, symbols.Single(s => s.Name == "Second").Line);
        Assert.DoesNotContain(symbols, s => s.Kind == SymbolKind.Property);
    }

    [Fact]
    public void CSharp_MethodInsideClass_HasContainingTypeAndFullName()
    {
        const string source = """
            namespace N;

            public sealed class Calc
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """;

        List<SymbolEntry> symbols = new CSharpSymbolParser().Parse("Calc.cs", source).ToList();

        SymbolEntry add = symbols.Single(s => s.Name == "Add" && s.Kind == SymbolKind.Method);
        Assert.Equal("Calc", add.ContainingType);
        Assert.Equal("Calc.Add", add.FullName);
    }

    [Fact]
    public void TypeScript_ClassMethodInterfaceAliasAndFunctions_AreIndexed()
    {
        const string source = """
            export class OrderService {
                constructor() {}
                public total(x: number): number {
                    return x;
                }
            }
            export interface IOrder { id: string }
            export type OrderId = string;
            export function loadOrders(): void {
            }
            export const formatOrder = (o: IOrder) => o.id;
            """;

        List<SymbolEntry> symbols = new TypeScriptSymbolParser().Parse("orders.ts", source).ToList();

        Assert.Contains(symbols, s => s.Name == "OrderService" && s.Kind == SymbolKind.Class);
        Assert.Contains(symbols, s => s.Name == "IOrder" && s.Kind == SymbolKind.Interface);
        Assert.Contains(symbols, s => s.Name == "OrderId" && s.Kind == SymbolKind.TypeAlias);
        Assert.Contains(symbols, s => s.Name == "loadOrders" && s.Kind == SymbolKind.Function);
        Assert.Contains(symbols, s => s.Name == "formatOrder" && s.Kind == SymbolKind.Function);

        SymbolEntry total = symbols.Single(s => s.Name == "total");
        Assert.Equal(SymbolKind.Method, total.Kind);
        Assert.Equal("OrderService", total.ContainingType);
        Assert.Equal("OrderService.total", total.FullName);
    }

    [Fact]
    public void TypeScript_ConstructorIsNotIndexedAsMethod()
    {
        const string source = """
            export class Box {
                constructor() {}
            }
            """;

        List<SymbolEntry> symbols = new TypeScriptSymbolParser().Parse("box.ts", source).ToList();

        Assert.DoesNotContain(symbols, s => s.Name == "constructor");
    }

    [Fact]
    public void Python_ClassAndAsyncFunction_AreIndexed()
    {
        const string source = """
            class Parser:
                pass

            async def fetch(url):
                return url
            """;

        List<SymbolEntry> symbols = new GenericSymbolParser().Parse("parser.py", source).ToList();

        SymbolEntry cls = symbols.Single(s => s.Name == "Parser");
        Assert.Equal(SymbolKind.Class, cls.Kind);
        Assert.Equal(1, cls.Line);

        SymbolEntry fn = symbols.Single(s => s.Name == "fetch");
        Assert.Equal(SymbolKind.Function, fn.Kind);
        Assert.Equal(4, fn.Line);
    }

    [Fact]
    public void Go_FunctionAndMethodWithReceiver_AreIndexed()
    {
        const string source = """
            package store

            func (s *Store) Save(x int) error {
                return nil
            }

            func Load() {
            }
            """;

        List<SymbolEntry> symbols = new GenericSymbolParser().Parse("store.go", source).ToList();

        Assert.Contains(symbols, s => s.Name == "Save" && s.Kind == SymbolKind.Function);
        Assert.Contains(symbols, s => s.Name == "Load" && s.Kind == SymbolKind.Function);
    }

    [Fact]
    public void Rust_StructAndFunction_AreIndexed()
    {
        const string source = """
            pub struct Config {
                pub value: u32,
            }

            pub fn run(x: u32) -> u32 {
                x
            }
            """;

        List<SymbolEntry> symbols = new GenericSymbolParser().Parse("lib.rs", source).ToList();

        Assert.Contains(symbols, s => s.Name == "Config" && s.Kind == SymbolKind.Class);
        Assert.Contains(symbols, s => s.Name == "run" && s.Kind == SymbolKind.Function);
    }

    [Fact]
    public void Java_ClassAndMethod_AreIndexed()
    {
        const string source = """
            public class Widget {
                public void render(int x) {
                }
            }
            """;

        List<SymbolEntry> symbols = new GenericSymbolParser().Parse("Widget.java", source).ToList();

        Assert.Contains(symbols, s => s.Name == "Widget" && s.Kind == SymbolKind.Class);
        Assert.Contains(symbols, s => s.Name == "render" && s.Kind == SymbolKind.Method);
    }

    [Fact]
    public void Parsers_SameInput_ProduceStableIds()
    {
        const string source = "public sealed class Stable\n{\n}\n";

        string first = new CSharpSymbolParser().Parse("Stable.cs", source).Single(s => s.Name == "Stable").Id;
        string second = new CSharpSymbolParser().Parse("Stable.cs", source).Single(s => s.Name == "Stable").Id;

        Assert.Equal(first, second);
    }
}
