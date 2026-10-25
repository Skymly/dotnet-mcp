using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Xunit.Abstractions;

namespace S9.Verification;

public class Q2_MvvmGeneratorTests
{
    private readonly ITestOutputHelper _output;

    public Q2_MvvmGeneratorTests(ITestOutputHelper output) => _output = output;

    private static async Task<(
        Compilation Compilation,
        ImmutableArray<SyntaxTree> GeneratedTrees,
        ImmutableArray<GeneratorInfo> Generators,
        INamedTypeSymbol Vm)> LoadAsync()
    {
        var (_, compilation, _, generators, generatedTrees) = await FixtureWorkspace.OpenAsync();
        var vm = compilation.GetTypeByMetadataName("ToolkitApp.MainViewModel")
            ?? throw new InvalidOperationException("MainViewModel not found.");
        return (compilation, generatedTrees, generators, vm);
    }

    private string DescribeLocation(ISymbol symbol, ImmutableArray<SyntaxTree> generatedTrees)
    {
        var parts = symbol.DeclaringSyntaxReferences.Select(r =>
        {
            var generated = FixtureWorkspace.IsGeneratedTree(r.SyntaxTree, generatedTrees);
            var path = r.SyntaxTree.FilePath.Replace('\\', '/');
            var shortPath = generated
                ? string.Join('/', path.Split('/').TakeLast(4))
                : path.Split('/').Last();
            return $"{(generated ? "GENERATED" : "hand")}:{shortPath}@{r.Span.Start}";
        });
        return string.Join(" | ", parts);
    }

    private void DumpMembers(INamedTypeSymbol type, ImmutableArray<SyntaxTree> generatedTrees)
    {
        foreach (var member in type.GetMembers().OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            var extra = member switch
            {
                IMethodSymbol m when m.MethodKind == MethodKind.Ordinary =>
                    $" IsPartialDef={m.IsPartialDefinition} ImplPart={(m.PartialImplementationPart is null ? "-" : "set")}",
                IPropertySymbol p =>
                    $" ImplPart={(p.PartialImplementationPart is null ? "-" : "set")} DefPart={(p.PartialDefinitionPart is null ? "-" : "set")}",
                _ => "",
            };
            _output.WriteLine($"{member.Kind,-12} {member.Name,-28}{extra} @ {DescribeLocation(member, generatedTrees)}");
        }
    }

    [Fact]
    public async Task Enumerate_All_Generators_In_Fixture()
    {
        var (_, _, generators, _) = await LoadAsync();
        foreach (var g in generators)
        {
            _output.WriteLine($"{g.IdentityString}  file={g.FileVersion} info={g.InformationalVersion}");
        }

        Assert.Contains(generators, g =>
            g.AssemblyName == "CommunityToolkit.Mvvm.SourceGenerators" &&
            g.TypeFullName == "CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator");
        Assert.Contains(generators, g =>
            g.AssemblyName == "CommunityToolkit.Mvvm.SourceGenerators" &&
            g.TypeFullName == "CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator");
    }

    [Fact]
    public async Task ObservableProperty_Generates_Named_Properties_In_Generated_Trees()
    {
        var (_, generatedTrees, _, vm) = await LoadAsync();
        DumpMembers(vm, generatedTrees);

        // Field name -> generated property name convention evidence.
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["_name"] = "Name",
            ["m_city"] = "City",
            ["plain"] = "Plain",
            ["isActive"] = "IsActive",
        };

        foreach (var (field, property) in expected)
        {
            var fieldSymbol = vm.GetMembers(field).OfType<IFieldSymbol>().SingleOrDefault();
            var propSymbol = vm.GetMembers(property).OfType<IPropertySymbol>().SingleOrDefault();
            Assert.NotNull(fieldSymbol);
            Assert.NotNull(propSymbol);

            var fieldGenerated = FixtureWorkspace.IsGeneratedTree(
                fieldSymbol.DeclaringSyntaxReferences[0].SyntaxTree, generatedTrees);
            var propGenerated = FixtureWorkspace.IsGeneratedTree(
                propSymbol.DeclaringSyntaxReferences[0].SyntaxTree, generatedTrees);

            _output.WriteLine($"{field,-10} (generated={fieldGenerated}) -> {property} (generated={propGenerated})");
            Assert.False(fieldGenerated);   // the trigger field is handwritten
            Assert.True(propGenerated);     // the produced property is generated

            // The trigger field carries the attribute; the generated property does not.
            Assert.Contains(fieldSymbol.GetAttributes(),
                a => a.AttributeClass?.Name == "ObservablePropertyAttribute");
            Assert.DoesNotContain(propSymbol.GetAttributes(),
                a => a.AttributeClass?.Name == "ObservablePropertyAttribute");
        }
    }

    [Fact]
    public async Task ObservableProperty_On_PartialProperty_Pairs_Via_Roslyn_Api()
    {
        var (_, generatedTrees, _, vm) = await LoadAsync();

        // GetMembers surfaces ONE symbol for the partial property pair (same as methods).
        var member = vm.GetMembers("Nick").OfType<IPropertySymbol>().Single();
        var defRefTrees = member.DeclaringSyntaxReferences.Select(r => r.SyntaxTree.FilePath);
        _output.WriteLine($"Nick member: DeclRefs={member.DeclaringSyntaxReferences.Length}, " +
                          $"ImplPart={(member.PartialImplementationPart is null ? "null" : "set")}, " +
                          $"trees={string.Join(", ", defRefTrees.Select(p => p.Split('/').Last()))}");

        // Find the generated implementation part and follow it back to the handwritten definition.
        var impl = member.PartialImplementationPart;
        if (impl is not null)
        {
            var implTree = impl.DeclaringSyntaxReferences[0].SyntaxTree;
            _output.WriteLine($"impl declared in {implTree.FilePath}");
            Assert.True(FixtureWorkspace.IsGeneratedTree(implTree, generatedTrees));
            var def = impl.PartialDefinitionPart ?? impl;
            var defTree = def.DeclaringSyntaxReferences.Single(r =>
                !FixtureWorkspace.IsGeneratedTree(r.SyntaxTree, generatedTrees)).SyntaxTree;
            Assert.EndsWith("ViewModels.cs", defTree.FilePath.Replace('\\', '/'));
            return;
        }

        // Or the member itself may be the implementation part (GetMembers surfaces either part).
        var defPart = member.PartialDefinitionPart;
        Assert.NotNull(defPart);
        var handTree = defPart.DeclaringSyntaxReferences.Single(r =>
            !FixtureWorkspace.IsGeneratedTree(r.SyntaxTree, generatedTrees)).SyntaxTree;
        Assert.EndsWith("ViewModels.cs", handTree.FilePath.Replace('\\', '/'));
    }

    [Fact]
    public async Task ObservableProperty_Callbacks_Are_Generated_Definitions()
    {
        var (_, generatedTrees, _, vm) = await LoadAsync();

        foreach (var name in new[] { "OnNameChanging", "OnNameChanged" })
        {
            var members = vm.GetMembers(name).OfType<IMethodSymbol>().ToList();
            _output.WriteLine($"{name}: {members.Count} overload(s)");
            foreach (var member in members)
            {
                var tree = member.DeclaringSyntaxReferences[0].SyntaxTree;
                var generated = FixtureWorkspace.IsGeneratedTree(tree, generatedTrees);
                _output.WriteLine($"  {member.ToDisplayString()} IsPartialDefinition={member.IsPartialDefinition}, " +
                                  $"generated={generated}, " +
                                  $"ImplPart={(member.PartialImplementationPart is null ? "null" : member.PartialImplementationPart.DeclaringSyntaxReferences[0].SyntaxTree.FilePath.Split('/').Last())}");
            }
        }

        // The toolkit emits TWO definition overloads per callback: (T value) and (T old, T new).
        var changingOverloads = vm.GetMembers("OnNameChanging").OfType<IMethodSymbol>().ToList();
        var changedOverloads = vm.GetMembers("OnNameChanged").OfType<IMethodSymbol>().ToList();
        Assert.Equal(2, changingOverloads.Count);
        Assert.Equal(2, changedOverloads.Count);

        // Every callback definition lives in a generated tree.
        Assert.All(changingOverloads.Concat(changedOverloads), m =>
            Assert.True(FixtureWorkspace.IsGeneratedTree(
                m.DeclaringSyntaxReferences[0].SyntaxTree, generatedTrees)));

        // The user implemented only OnNameChanged(string): that one overload has a
        // handwritten implementation part; all other definitions stay unimplemented.
        var implemented = changedOverloads.Single(m => m.Parameters.Length == 1);
        Assert.NotNull(implemented.PartialImplementationPart);
        Assert.EndsWith("ViewModels.cs",
            implemented.PartialImplementationPart!.DeclaringSyntaxReferences[0]
                .SyntaxTree.FilePath.Replace('\\', '/'));

        var unimplemented = changedOverloads.Single(m => m.Parameters.Length == 2);
        Assert.Null(unimplemented.PartialImplementationPart);
        Assert.All(changingOverloads, m => Assert.Null(m.PartialImplementationPart));
    }

    [Fact]
    public async Task RelayCommand_Generates_Command_Properties_With_Convention_Names()
    {
        var (_, generatedTrees, _, vm) = await LoadAsync();

        var commandMembers = vm.GetMembers()
            .Where(m => m.Name.EndsWith("Command", StringComparison.Ordinal)
                && (m is IPropertySymbol or IFieldSymbol))
            .ToList();
        foreach (var m in commandMembers)
        {
            var tree = m.DeclaringSyntaxReferences[0].SyntaxTree;
            _output.WriteLine($"{m.Kind} {m.Name} type={(m is IPropertySymbol p ? p.Type.ToDisplayString() : ((IFieldSymbol)m).Type.ToDisplayString())} " +
                              $"generated={FixtureWorkspace.IsGeneratedTree(tree, generatedTrees)}");
        }

        // Save -> SaveCommand (IRelayCommand)
        var save = Assert.Single(vm.GetMembers("SaveCommand").OfType<IPropertySymbol>());
        // LoadAsync -> LoadCommand (Async suffix stripped; IAsyncRelayCommand for Task)
        var load = Assert.Single(vm.GetMembers("LoadCommand").OfType<IPropertySymbol>());
        _output.WriteLine($"SaveCommand: {save.Type.ToDisplayString()}; LoadCommand: {load.Type.ToDisplayString()}");

        // OnRefresh -> RefreshCommand: the generator strips the `On` prefix.
        var refreshMembers = vm.GetMembers()
            .Where(m => m.Name.Contains("Refresh", StringComparison.Ordinal))
            .Select(m => $"{m.Kind}:{m.Name}")
            .ToList();
        _output.WriteLine($"Refresh members: {string.Join(", ", refreshMembers)}");
        Assert.Single(vm.GetMembers("RefreshCommand").OfType<IPropertySymbol>());
        Assert.Empty(vm.GetMembers("OnRefreshCommand"));
    }

    [Fact]
    public async Task Convention_Inverse_Rules_Find_Unique_Triggers()
    {
        // Mini rule engine: generated member name -> candidate handwritten names ->
        // require the candidate to carry the trigger attribute -> unique hit.
        var (_, generatedTrees, _, vm) = await LoadAsync();

        static string LowerFirst(string name) =>
            name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

        // [ObservableProperty] property -> field candidates.
        foreach (var property in new[] { "Name", "City", "Plain", "IsActive" })
        {
            var candidates = new[]
            {
                LowerFirst(property), $"_{LowerFirst(property)}",
                $"m_{LowerFirst(property)}", $"m_{property}",
            };
            var hits = candidates
                .SelectMany(c => vm.GetMembers(c))
                .OfType<IFieldSymbol>()
                .Where(f => f.GetAttributes().Any(a => a.AttributeClass?.Name == "ObservablePropertyAttribute"))
                .Distinct(SymbolEqualityComparer.Default)
                .ToList();
            var hit = Assert.Single(hits)!;
            _output.WriteLine($"{property} -> {hit.Name}");
        }

        // [RelayCommand] XxxCommand property -> method candidates.
        foreach (var command in new[] { "SaveCommand", "LoadCommand", "RefreshCommand" })
        {
            var stem = command[..^"Command".Length];
            var candidates = new[]
            {
                stem, $"On{stem}", $"{stem}Async", $"On{stem}Async",
            };
            var hits = candidates
                .SelectMany(c => vm.GetMembers(c))
                .OfType<IMethodSymbol>()
                .Where(m => m.GetAttributes().Any(a => a.AttributeClass?.Name == "RelayCommandAttribute"))
                .Distinct(SymbolEqualityComparer.Default)
                .ToList();
            var hit = Assert.Single(hits)!;
            _output.WriteLine($"{command} -> {hit.Name} ({hit.DeclaringSyntaxReferences[0].SyntaxTree.FilePath.Split('/').Last()})");
            Assert.False(FixtureWorkspace.IsGeneratedTree(
                hit.DeclaringSyntaxReferences[0].SyntaxTree, generatedTrees));
        }
    }
}
