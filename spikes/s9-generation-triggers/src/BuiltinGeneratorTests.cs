using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Xunit.Abstractions;

namespace S9.Verification;

public class BuiltinGeneratorTests
{
    private readonly ITestOutputHelper _output;

    public BuiltinGeneratorTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task GeneratedRegex_Handwritten_Definition_Links_To_Generated_Implementation()
    {
        var (_, compilation, _, _, generatedTrees) = await FixtureWorkspace.OpenAsync();
        var type = compilation.GetTypeByMetadataName("ToolkitApp.Regexes")
            ?? throw new InvalidOperationException("Regexes not found.");

        // GetMembers surfaces the handwritten *definition* part; the generator emitted
        // the implementation part (same mechanism as [ObservableProperty] partial props).
        var foo = type.GetMembers("Foo").OfType<IMethodSymbol>().Single();
        _output.WriteLine($"Foo: IsPartialDefinition={foo.IsPartialDefinition}, " +
                          $"DeclRefs={foo.DeclaringSyntaxReferences.Length}, " +
                          $"ImplPart={(foo.PartialImplementationPart is null ? "null" : "set")}");

        Assert.True(foo.IsPartialDefinition);
        Assert.NotNull(foo.PartialImplementationPart);

        var impl = foo.PartialImplementationPart!;
        var implTree = impl.DeclaringSyntaxReferences[0].SyntaxTree;
        _output.WriteLine($"impl tree: {implTree.FilePath}");
        Assert.True(FixtureWorkspace.IsGeneratedTree(implTree, generatedTrees));
        var parsed = FixtureWorkspace.ParseGeneratedFilePath(implTree.FilePath);
        _output.WriteLine($"parsed: {parsed.Assembly}/{parsed.Type}/{parsed.Hint}");
        Assert.Equal("System.Text.RegularExpressions.Generator", parsed.Assembly);
        Assert.Equal("System.Text.RegularExpressions.Generator.RegexGenerator", parsed.Type);

        // From the generated implementation, the handwritten definition is Exact.
        var def = impl.PartialDefinitionPart;
        Assert.NotNull(def);
        Assert.EndsWith("Regexes.cs",
            def!.DeclaringSyntaxReferences[0].SyntaxTree.FilePath.Replace('\\', '/'));

        // The trigger attribute sits on the handwritten definition declaration.
        Assert.Contains(foo.GetAttributes(), a => a.AttributeClass?.Name == "GeneratedRegexAttribute");
    }
}
