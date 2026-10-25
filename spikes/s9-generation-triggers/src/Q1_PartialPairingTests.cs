using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Xunit.Abstractions;
using MethodBlockSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax.MethodBlockSyntax;

namespace S9.Verification;

internal static class SymbolAssert
{
    public static void SameSymbol(ISymbol expected, ISymbol? actual)
    {
        Assert.NotNull(actual);
        Assert.True(
            SymbolEqualityComparer.Default.Equals(expected, actual),
            $"Expected {expected.ToDisplayString()}, got {actual.ToDisplayString()}.");
    }
}

public class Q1_PartialPairingTests
{
    private readonly ITestOutputHelper _output;

    public Q1_PartialPairingTests(ITestOutputHelper output) => _output = output;

    private static TSymbol DeclaredSymbol<TSymbol>(
        Compilation compilation,
        string path,
        Func<SyntaxNode, bool>? nodeFilter = null)
        where TSymbol : ISymbol
    {
        var tree = compilation.SyntaxTrees.Single(t => t.FilePath == path);
        var model = compilation.GetSemanticModel(tree);
        var node = tree.GetRoot().DescendantNodes()
            .First(n => (nodeFilter is null || nodeFilter(n)) && n is
                MethodDeclarationSyntax
                or PropertyDeclarationSyntax
                or EventDeclarationSyntax
                or ConstructorDeclarationSyntax
                or MethodBlockSyntax
                or MethodStatementSyntax
                // Field-like event declarations: the symbol lives on the variable declarator.
                or Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax
                    { Parent.Parent: EventFieldDeclarationSyntax });
        var symbol = model.GetDeclaredSymbol(node);
        return Assert.IsAssignableFrom<TSymbol>(symbol);
    }

    [Fact]
    public void CSharp_PartialMethod_TwoParts_LinkBothDirections()
    {
        var compilation = InMemory.CompileCSharp(
            ("def.cs", "public partial class C { partial void M(); }"),
            ("impl.cs", "public partial class C { partial void M() { } }"));

        var def = DeclaredSymbol<IMethodSymbol>(compilation, "def.cs");
        var impl = DeclaredSymbol<IMethodSymbol>(compilation, "impl.cs");

        _output.WriteLine($"def: PartialDefinitionPart={def.PartialDefinitionPart?.ToDisplayString() ?? "null"}, " +
                          $"PartialImplementationPart={def.PartialImplementationPart?.ToDisplayString() ?? "null"}, " +
                          $"IsPartialDefinition={def.IsPartialDefinition}");
        _output.WriteLine($"impl: PartialDefinitionPart={impl.PartialDefinitionPart?.ToDisplayString() ?? "null"}, " +
                          $"PartialImplementationPart={impl.PartialImplementationPart?.ToDisplayString() ?? "null"}, " +
                          $"IsPartialDefinition={impl.IsPartialDefinition}");

        // Definition -> implementation link exists; implementation -> definition link exists.
        Assert.Null(def.PartialDefinitionPart);
        SymbolAssert.SameSymbol(impl, def.PartialImplementationPart);
        SymbolAssert.SameSymbol(def, impl.PartialDefinitionPart);
        Assert.Null(impl.PartialImplementationPart);
        Assert.True(def.IsPartialDefinition);
        Assert.False(impl.IsPartialDefinition);

        // GetMembers merges the pair into ONE member symbol.
        var members = compilation.GetTypeByMetadataName("C")!.GetMembers("M").OfType<IMethodSymbol>().ToList();
        var member = Assert.Single(members);
        _output.WriteLine($"GetMembers(\"M\") -> {members.Count} symbol; " +
                          $"IsPartialDefinition={member.IsPartialDefinition}, " +
                          $"DefPart={(member.PartialDefinitionPart is null ? "null" : "set")}, " +
                          $"ImplPart={(member.PartialImplementationPart is null ? "null" : "set")}, " +
                          $"DeclRefs={member.DeclaringSyntaxReferences.Length}");
        // The surfaced member IS the definition part: one decl ref (the definition declaration),
        // IsPartialDefinition=true, PartialImplementationPart set.
        Assert.True(member.IsPartialDefinition);
        Assert.NotNull(member.PartialImplementationPart);
        Assert.Single(member.DeclaringSyntaxReferences);
        Assert.Equal("def.cs", member.DeclaringSyntaxReferences[0].SyntaxTree.FilePath);
    }

    [Fact]
    public void CSharp_PartialMethod_DefinitionOnly_HasNullImplementation()
    {
        var compilation = InMemory.CompileCSharp(
            ("def.cs", "public partial class C { partial void M(); }"));

        var def = DeclaredSymbol<IMethodSymbol>(compilation, "def.cs");
        Assert.Null(def.PartialDefinitionPart);
        Assert.Null(def.PartialImplementationPart);
        Assert.True(def.IsPartialDefinition);
    }

    [Fact]
    public void CSharp_NonPartialMember_BothPropertiesNull()
    {
        var compilation = InMemory.CompileCSharp(
            ("a.cs", "public class C { void M() { } int P { get; set; } }"));

        var method = DeclaredSymbol<IMethodSymbol>(compilation, "a.cs");
        Assert.Null(method.PartialDefinitionPart);
        Assert.Null(method.PartialImplementationPart);
        Assert.False(method.IsPartialDefinition);

        var property = compilation.GetTypeByMetadataName("C")!.GetMembers("P").OfType<IPropertySymbol>().Single();
        Assert.Null(property.PartialDefinitionPart);
        Assert.Null(property.PartialImplementationPart);
    }

    [Fact]
    public void CSharp_PartialProperty_LinksBothDirections()
    {
        var compilation = InMemory.CompileCSharp(
            ("def.cs", "public partial class C { public partial int P { get; } }"),
            ("impl.cs", "public partial class C { public partial int P { get => 42; } }"));

        var def = DeclaredSymbol<IPropertySymbol>(compilation, "def.cs");
        var impl = DeclaredSymbol<IPropertySymbol>(compilation, "impl.cs");

        _output.WriteLine($"def: PartialDefinitionPart={def.PartialDefinitionPart}, PartialImplementationPart={def.PartialImplementationPart}");
        _output.WriteLine($"impl: PartialDefinitionPart={impl.PartialDefinitionPart}, PartialImplementationPart={impl.PartialImplementationPart}");

        Assert.Null(def.PartialDefinitionPart);
        SymbolAssert.SameSymbol(impl, def.PartialImplementationPart);
        SymbolAssert.SameSymbol(def, impl.PartialDefinitionPart);
        Assert.Null(impl.PartialImplementationPart);
    }

    [Fact]
    public void CSharp14_PartialEvent_LinksBothDirections()
    {
        var compilation = InMemory.CompileCSharp(
            ("def.cs", "using System; public partial class C { public partial event EventHandler E; }"),
            ("impl.cs", "using System; public partial class C { public partial event EventHandler E { add { } remove { } } }"));

        var def = DeclaredSymbol<IEventSymbol>(compilation, "def.cs");
        var impl = DeclaredSymbol<IEventSymbol>(compilation, "impl.cs");

        _output.WriteLine($"def: PartialDefinitionPart={def.PartialDefinitionPart}, PartialImplementationPart={def.PartialImplementationPart}");
        _output.WriteLine($"impl: PartialDefinitionPart={impl.PartialDefinitionPart}, PartialImplementationPart={impl.PartialImplementationPart}");

        Assert.Null(def.PartialDefinitionPart);
        SymbolAssert.SameSymbol(impl, def.PartialImplementationPart);
        SymbolAssert.SameSymbol(def, impl.PartialDefinitionPart);
        Assert.Null(impl.PartialImplementationPart);
    }

    [Fact]
    public void CSharp14_PartialConstructor_LinksBothDirections()
    {
        var compilation = InMemory.CompileCSharp(
            ("def.cs", "public partial class C { public partial C(); }"),
            ("impl.cs", "public partial class C { public partial C() { } }"));

        var def = DeclaredSymbol<IMethodSymbol>(compilation, "def.cs");
        var impl = DeclaredSymbol<IMethodSymbol>(compilation, "impl.cs");

        _output.WriteLine($"def: MethodKind={def.MethodKind}, PartialImplementationPart={def.PartialImplementationPart}");
        _output.WriteLine($"impl: MethodKind={impl.MethodKind}, PartialDefinitionPart={impl.PartialDefinitionPart}");

        Assert.Equal(MethodKind.Constructor, def.MethodKind);
        SymbolAssert.SameSymbol(impl, def.PartialImplementationPart);
        SymbolAssert.SameSymbol(def, impl.PartialDefinitionPart);
    }

    [Fact]
    public void Generated_ImplementationPart_ResolvesToHandwrittenDefinition()
    {
        var handwritten = InMemory.CompileCSharp(
            ("hand.cs", "partial class Gen { partial void Hook(); }"));

        // Generated trees are re-parsed with the driver's parse options — they must match the
        // compilation's language version or AddSyntaxTrees throws ("inconsistent language version").
        var parseOptions = (CSharpParseOptions)handwritten.SyntaxTrees.Single().Options;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new ISourceGenerator[] { new EmitPartialImplementationGenerator() },
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            handwritten, out var updated, out var diagnostics);

        var generatedTree = updated.SyntaxTrees.Single(t => t.FilePath.EndsWith("Gen.impl.g.cs"));
        var generatedSymbol = DeclaredSymbol<IMethodSymbol>(updated, generatedTree.FilePath);

        var definition = generatedSymbol.PartialDefinitionPart;
        Assert.NotNull(definition);
        var defRef = definition.DeclaringSyntaxReferences.Single();
        _output.WriteLine($"generated impl -> definition at {defRef.SyntaxTree.FilePath}:{defRef.Span}");

        Assert.Equal("hand.cs", defRef.SyntaxTree.FilePath);
        Assert.False(defRef.SyntaxTree.FilePath.EndsWith(".g.cs", StringComparison.Ordinal));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Generated_DefinitionPart_ResolvesToHandwrittenImplementation()
    {
        // Mirrors the toolkit pattern: generator emits `partial void OnX();` definitions,
        // user writes the implementation in handwritten code.
        var handwritten = InMemory.CompileCSharp(
            ("hand.cs", "partial class Gen { partial void Hook() { } }"));

        var parseOptions = (CSharpParseOptions)handwritten.SyntaxTrees.Single().Options;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new ISourceGenerator[] { new EmitPartialDefinitionGenerator() },
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            handwritten, out var updated, out var diagnostics);

        var generatedTree = updated.SyntaxTrees.Single(t => t.FilePath.EndsWith("Gen.def.g.cs"));
        var generatedSymbol = DeclaredSymbol<IMethodSymbol>(updated, generatedTree.FilePath);

        _output.WriteLine($"generated def: IsPartialDefinition={generatedSymbol.IsPartialDefinition}, " +
                          $"PartialImplementationPart={generatedSymbol.PartialImplementationPart}");

        Assert.True(generatedSymbol.IsPartialDefinition);
        var impl = generatedSymbol.PartialImplementationPart;
        Assert.NotNull(impl);
        var implRef = impl.DeclaringSyntaxReferences.Single();
        Assert.Equal("hand.cs", implRef.SyntaxTree.FilePath);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void VB_PartialMethod_LinksBothDirections()
    {
        var compilation = InMemory.CompileVisualBasic(
            ("def.vb", """
                Partial Class C
                    Partial Private Sub M()
                    End Sub
                End Class
                """),
            ("impl.vb", """
                Partial Class C
                    Private Sub M()
                    End Sub
                End Class
                """));

        var def = DeclaredSymbol<IMethodSymbol>(compilation, "def.vb");
        var impl = DeclaredSymbol<IMethodSymbol>(compilation, "impl.vb");

        _output.WriteLine($"VB def: PartialImplementationPart={def.PartialImplementationPart}, IsPartialDefinition={def.IsPartialDefinition}");
        _output.WriteLine($"VB impl: PartialDefinitionPart={impl.PartialDefinitionPart}, IsPartialDefinition={impl.IsPartialDefinition}");

        SymbolAssert.SameSymbol(impl, def.PartialImplementationPart);
        SymbolAssert.SameSymbol(def, impl.PartialDefinitionPart);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Navigation_Rule_FromAnyPart_ToDefinition()
    {
        // Product rule: from any partial symbol, `PartialDefinitionPart ?? self` yields the
        // definition part; then check its DeclaringSyntaxReferences for a handwritten tree.
        var compilation = InMemory.CompileCSharp(
            ("def.cs", "public partial class C { public partial int P { get; } partial void M(); }"),
            ("impl.cs", "public partial class C { public partial int P { get => 42; } partial void M() { } }"));

        var propImpl = DeclaredSymbol<IPropertySymbol>(
            compilation, "impl.cs", n => n is PropertyDeclarationSyntax);
        var methodImpl = DeclaredSymbol<IMethodSymbol>(
            compilation, "impl.cs", n => n is MethodDeclarationSyntax m && m.Identifier.Text == "M");

        static ISymbol ToDefinition(ISymbol symbol) => symbol switch
        {
            IMethodSymbol m => (ISymbol?)m.PartialDefinitionPart ?? m,
            IPropertySymbol p => (ISymbol?)p.PartialDefinitionPart ?? p,
            IEventSymbol e => (ISymbol?)e.PartialDefinitionPart ?? e,
            _ => symbol,
        };

        var propDef = ToDefinition(propImpl);
        var methodDef = ToDefinition(methodImpl);
        Assert.Equal("def.cs", propDef.DeclaringSyntaxReferences.Single().SyntaxTree.FilePath);
        Assert.Equal("def.cs", methodDef.DeclaringSyntaxReferences.Single().SyntaxTree.FilePath);
    }
}
