using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit.Abstractions;

namespace S9.Verification;

public class Q3_StjGeneratorTests
{
    private readonly ITestOutputHelper _output;

    public Q3_StjGeneratorTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task JsonContext_Generated_Members_Live_In_Generated_Trees()
    {
        var (_, compilation, docs, generators, generatedTrees) = await FixtureWorkspace.OpenAsync();
        var context = compilation.GetTypeByMetadataName("ToolkitApp.AppJsonContext")
            ?? throw new InvalidOperationException("AppJsonContext not found.");

        _output.WriteLine($"generated docs for context region:");
        foreach (var doc in docs)
        {
            var parsed = FixtureWorkspace.ParseGeneratedFilePath(doc.FilePath ?? "");
            if (doc.Name.Contains("JsonContext", StringComparison.Ordinal) ||
                doc.Name.Contains("AppJsonContext", StringComparison.Ordinal) ||
                parsed.Type?.Contains("Json") == true)
            {
                _output.WriteLine($"  {doc.Name}  FilePath=...{doc.FilePath?.Split("generated")[^1]}");
            }
        }

        var generatedMembers = context.GetMembers()
            .Where(m => m.DeclaringSyntaxReferences.Any(r =>
                FixtureWorkspace.IsGeneratedTree(r.SyntaxTree, generatedTrees)))
            .ToList();
        var handwrittenMembers = context.GetMembers()
            .Where(m => m.DeclaringSyntaxReferences.Any() && m.DeclaringSyntaxReferences.All(r =>
                !FixtureWorkspace.IsGeneratedTree(r.SyntaxTree, generatedTrees)))
            .ToList();

        _output.WriteLine($"generated members ({generatedMembers.Count}):");
        foreach (var m in generatedMembers.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            _output.WriteLine($"  {m.Kind,-10} {m.Name}");
        }

        _output.WriteLine($"handwritten-only members ({handwrittenMembers.Count}):");
        foreach (var m in handwrittenMembers)
        {
            _output.WriteLine($"  {m.Kind,-10} {m.Name}");
        }

        // The generator emits real members into the same partial class.
        Assert.NotEmpty(generatedMembers);
        // Generated members are NOT partial members — member-level pairing doesn't apply;
        // the trigger is the attribute on the containing type's handwritten declaration.
        Assert.All(generatedMembers.OfType<IPropertySymbol>(),
            p => Assert.Null(p.PartialDefinitionPart));
    }

    [Fact]
    public async Task JsonSerializable_Attribute_Resolves_To_Handwritten_Declaration()
    {
        var (_, compilation, _, _, generatedTrees) = await FixtureWorkspace.OpenAsync();
        var context = compilation.GetTypeByMetadataName("ToolkitApp.AppJsonContext")!;

        // The containing type merges handwritten + generated partial declarations.
        var refs = context.DeclaringSyntaxReferences;
        var handRefs = refs.Where(r => !FixtureWorkspace.IsGeneratedTree(r.SyntaxTree, generatedTrees)).ToList();
        var genRefs = refs.Where(r => FixtureWorkspace.IsGeneratedTree(r.SyntaxTree, generatedTrees)).ToList();
        _output.WriteLine($"AppJsonContext DeclRefs: {refs.Length} total, {handRefs.Count} handwritten, {genRefs.Count} generated");

        // Attribute lookup merges across partial declarations; ApplicationSyntaxReference
        // points at the *attribute syntax* in the handwritten tree = the trigger site.
        var attr = context.GetAttributes()
            .Single(a => a.AttributeClass?.Name == "JsonSerializableAttribute");
        var syntax = attr.ApplicationSyntaxReference!.GetSyntax();
        _output.WriteLine($"attribute node: {syntax} in {syntax.SyntaxTree.FilePath} @{syntax.Span}");

        Assert.EndsWith("JsonContext.cs", syntax.SyntaxTree.FilePath.Replace('\\', '/'));
        Assert.IsType<AttributeSyntax>(syntax);
    }
}
