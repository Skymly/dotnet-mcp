using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;
using Xunit.Abstractions;

namespace S1.Verification;

public sealed class Q5_CostTests
{
    private readonly ITestOutputHelper _output;

    public Q5_CostTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Medium_synthetic_project_driver_cost_is_measurable_and_on_demand()
    {
        // Build an AdhocWorkspace project with ~200 ObservableProperty-like handwritten stubs
        // plus the SampleApp analyzers copied via MSBuild SampleApp references where possible.
        // For cost isolation we time: (1) GetCompilationAsync on SampleApp, (2) strip+driver rerun.
        await using var session = await WorkspaceSession.OpenAsync(FixturePaths.SampleAppProject);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var memBefore = GC.GetTotalMemory(forceFullCollection: false);

        var sw = Stopwatch.StartNew();
        var compilation = await session.Project.GetCompilationAsync();
        sw.Stop();
        var compileMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var (_, runResult, _) = await AttributionHelpers.RunDriverOnBaseAsync(session.Project);
        sw.Stop();
        var driverMs = sw.ElapsedMilliseconds;
        var memAfterSample = GC.GetTotalMemory(forceFullCollection: false);

        _output.WriteLine($"SampleApp GetCompilationAsync={compileMs}ms; strip+driver={driverMs}ms; generatedSources={runResult.Results.Sum(r => r.GeneratedSources.Length)}; Δmem≈{(memAfterSample - memBefore) / 1024.0:F1} KiB");

        // Synthetic medium: create many trees and time a second driver run on an expanded compilation.
        var parseOptions = (CSharpParseOptions)(session.Project.ParseOptions ?? CSharpParseOptions.Default);
        var trees = new List<SyntaxTree>();
        for (var i = 0; i < 200; i++)
        {
            var code = $$"""
                using CommunityToolkit.Mvvm.ComponentModel;
                namespace SampleApp.Synth;
                public partial class SynthVm{{i}} : ObservableObject
                {
                    [ObservableProperty] private string _name{{i}} = "n";
                }
                """;
            trees.Add(CSharpSyntaxTree.ParseText(code, parseOptions, path: $"SynthVm{i}.cs"));
        }

        var (baseCompilation, _, generators) = await AttributionHelpers.RunDriverOnBaseAsync(session.Project);
        var expanded = baseCompilation.AddSyntaxTrees(trees);

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators.Select(g => g.Generator),
            parseOptions: parseOptions,
            optionsProvider: session.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider);

        var memBeforeMedium = GC.GetTotalMemory(forceFullCollection: false);
        sw.Restart();
        driver = driver.RunGeneratorsAndUpdateCompilation(expanded, out var updated, out _, CancellationToken.None);
        sw.Stop();
        var mediumDriverMs = sw.ElapsedMilliseconds;
        var memAfterMedium = GC.GetTotalMemory(forceFullCollection: false);
        var mediumSources = driver.GetRunResult().Results.Sum(r => r.GeneratedSources.Length);

        _output.WriteLine($"Medium(~200 extra VMs) driver={mediumDriverMs}ms; generatedSources={mediumSources}; updatedTrees={updated.SyntaxTrees.Count()}; Δmem≈{(memAfterMedium - memBeforeMedium) / 1024.0:F1} KiB");
        _output.WriteLine("Large (150-project) not measured in-spike; extrapolate per-project driver cost × cache miss rate. Memory: GC deltas above are indicative only (not WorkingSet).");

        Assert.True(driverMs >= 0);
        Assert.True(mediumDriverMs >= 0);
        Assert.True(mediumSources > runResult.Results.Sum(r => r.GeneratedSources.Length));

        _output.WriteLine("On-demand: driver scoped to single Project — compatible with (projectId, epoch) cache.");
        _ = compilation;
    }
}

public sealed class Q6_ReflectionIdentityTests
{
    private readonly ITestOutputHelper _output;

    public Q6_ReflectionIdentityTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Reflection_can_read_internal_SourceGeneratedDocument_Identity_shape()
    {
        await using var session = await WorkspaceSession.OpenAsync(FixturePaths.SampleAppProject);
        var (_, docs) = await AttributionHelpers.GetCompilationAndGeneratedDocsAsync(session.Project);
        Assert.NotEmpty(docs);

        var identityProp = typeof(SourceGeneratedDocument).GetProperty(
            "Identity", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(identityProp);
        _output.WriteLine($"Identity property: {identityProp!.PropertyType.FullName}, IsPublic={identityProp.GetMethod?.IsPublic}");

        var identities = new List<GeneratorIdentity>();
        foreach (var doc in docs)
        {
            var id = AttributionHelpers.TryReflectIdentity(doc);
            Assert.NotNull(id);
            identities.Add(id.Value);
            _output.WriteLine($"{id.Value.TypeName} @ {id.Value.AssemblyName} v{id.Value.AssemblyVersion}; Hint={id.Value.HintName}; IdentityFilePath={id.Value.IdentityFilePath}; Doc.FilePath={doc.FilePath}");
        }

        Assert.Contains(identities, i =>
            i.TypeName.Contains("ObservablePropertyGenerator", StringComparison.Ordinal));

        // Guard: expected shape fields exist on the internal identity type.
        var sample = identityProp.GetValue(docs[0])!;
        var generator = sample.GetType().GetProperty("Generator")!.GetValue(sample)!;
        foreach (var field in new[] { "AssemblyName", "AssemblyPath", "AssemblyVersion", "TypeName" })
        {
            Assert.True(generator.GetType().GetProperty(field) is not null, $"Missing Identity.Generator.{field}");
        }
    }
}

public sealed class Q7_SymbolAttributionChainTests
{
    private readonly ITestOutputHelper _output;

    public Q7_SymbolAttributionChainTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ObservableProperty_symbol_attributes_to_ObservablePropertyGenerator()
    {
        await using var session = await WorkspaceSession.OpenAsync(FixturePaths.SampleAppProject);
        var (compilation, docs) = await AttributionHelpers.GetCompilationAndGeneratedDocsAsync(session.Project);

        var vm = compilation.GetTypeByMetadataName("SampleApp.PersonViewModel");
        Assert.NotNull(vm);

        var nameProperty = vm!.GetMembers("Name").OfType<IPropertySymbol>().Single();
        _output.WriteLine($"Name property locations: {nameProperty.Locations.Length}");

        foreach (var loc in nameProperty.Locations)
        {
            _output.WriteLine($"  IsInSource={loc.IsInSource}, IsInMetadata={loc.IsInMetadata}, path={loc.SourceTree?.FilePath}");
        }

        var declaring = nameProperty.DeclaringSyntaxReferences.First();
        var tree = declaring.SyntaxTree;
        var isGenerated = docs.Any(d =>
        {
            var dt = d.GetSyntaxTreeAsync().GetAwaiter().GetResult();
            return dt is not null && AttributionHelpers.TreesMatch(tree, dt);
        });

        Assert.True(isGenerated, "Generated Name property should live in a source-generated document.");

        var attribution = await AttributionHelpers.ResolveGeneratorViaDriverAsync(session.Project, nameProperty);
        _output.WriteLine($"Attribution={attribution}");

        Assert.NotNull(attribution);
        Assert.Contains("ObservablePropertyGenerator", attribution, StringComparison.Ordinal);

        // Reflection cross-check
        var doc = docs.First(d =>
        {
            var dt = d.GetSyntaxTreeAsync().GetAwaiter().GetResult();
            return dt is not null && AttributionHelpers.TreesMatch(tree, dt);
        });
        var reflected = AttributionHelpers.TryReflectIdentity(doc);
        _output.WriteLine($"Reflected={reflected?.TypeName}");
        Assert.NotNull(reflected);
        Assert.Contains("ObservablePropertyGenerator", reflected.Value.TypeName, StringComparison.Ordinal);
    }
}

public sealed class Q8_PartialMemberAttributionTests
{
    private readonly ITestOutputHelper _output;

    public Q8_PartialMemberAttributionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Partial_type_attributes_members_individually_with_signature_keys()
    {
        await using var session = await WorkspaceSession.OpenAsync(FixturePaths.SampleAppProject);
        var (compilation, docs) = await AttributionHelpers.GetCompilationAndGeneratedDocsAsync(session.Project);
        var vm = compilation.GetTypeByMetadataName("SampleApp.PersonViewModel");
        Assert.NotNull(vm);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var member in vm!.GetMembers().Where(m => m is IPropertySymbol or IMethodSymbol))
        {
            if (member is IMethodSymbol { MethodKind: not MethodKind.Ordinary })
            {
                continue;
            }

            var key = member is IMethodSymbol method
                ? $"{method.Name}({string.Join(",", method.Parameters.Select(p => p.Type.ToDisplayString()))})"
                : member.Name;

            var declaring = member.DeclaringSyntaxReferences.FirstOrDefault();
            if (declaring is null)
            {
                map[key] = "None";
                continue;
            }

            var tree = declaring.SyntaxTree;
            var generatedDoc = docs.FirstOrDefault(d =>
            {
                var dt = d.GetSyntaxTreeAsync().GetAwaiter().GetResult();
                return dt is not null && AttributionHelpers.TreesMatch(tree, dt);
            });

            if (generatedDoc is null)
            {
                map[key] = "Handwritten";
            }
            else
            {
                var attr = await AttributionHelpers.ResolveGeneratorViaDriverAsync(session.Project, member);
                map[key] = attr ?? $"Generated:{generatedDoc.HintName}";
            }
        }

        foreach (var kv in map.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            _output.WriteLine($"{kv.Key} => {kv.Value}");
        }

        Assert.Equal("Handwritten", map["DisplayName"]);
        Assert.Equal("Handwritten", map["Format()"]);
        Assert.Equal("Handwritten", map["Format(string)"]);
        Assert.Contains(map, kv => kv.Key == "Name" && kv.Value.Contains("ObservablePropertyGenerator", StringComparison.Ordinal));
        Assert.Contains(map, kv => kv.Key == "Age" && kv.Value.Contains("ObservablePropertyGenerator", StringComparison.Ordinal));

        // Overload keys must not collide — both signatures present as distinct map entries.
        Assert.True(map.ContainsKey("Format()") && map.ContainsKey("Format(string)"));
        Assert.Equal("Handwritten", map["Format()"]);
        Assert.Equal("Handwritten", map["Format(string)"]);
    }
}

public sealed class Q9_AdhocWorkspaceTests
{
    private readonly ITestOutputHelper _output;

    public Q9_AdhocWorkspaceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task AdhocWorkspace_with_AnalyzerReferences_source_generation_behavior()
    {
        // Load generators from the built SampleApp project analyzer refs via MSBuild first,
        // then transplant into AdhocWorkspace to see if GetSourceGeneratedDocumentsAsync works.
        await using var msbuild = await WorkspaceSession.OpenAsync(FixturePaths.SampleAppProject);
        var analyzerRefs = msbuild.Project.AnalyzerReferences.ToImmutableArray();
        var metadataRefs = msbuild.Project.MetadataReferences.ToImmutableArray();
        var parseOptions = msbuild.Project.ParseOptions!;
        var compilationOptions = msbuild.Project.CompilationOptions!;

        using var adhoc = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Create(),
                name: "AdhocSample",
                assemblyName: "AdhocSample",
                language: LanguageNames.CSharp,
                parseOptions: parseOptions,
                compilationOptions: compilationOptions,
                metadataReferences: metadataRefs,
                analyzerReferences: analyzerRefs)
            .WithFilePath(Path.Combine(Path.GetTempPath(), "AdhocSample.csproj"));

        var project = adhoc.AddProject(projectInfo);
        var source = """
            using CommunityToolkit.Mvvm.ComponentModel;
            namespace AdhocSample;
            public partial class AdhocVm : ObservableObject
            {
                [ObservableProperty] private string _title = "t";
            }
            """;
        project = project.AddDocument("AdhocVm.cs", SourceText.From(source, Encoding.UTF8)).Project;
        Assert.True(adhoc.TryApplyChanges(project.Solution));
        project = adhoc.CurrentSolution.GetProject(project.Id)!;

        var docs = (await project.GetSourceGeneratedDocumentsAsync()).ToImmutableArray();
        _output.WriteLine($"AdhocWorkspace generated docs: {docs.Length}");
        foreach (var d in docs.OfType<SourceGeneratedDocument>())
        {
            _output.WriteLine($"  {d.HintName} => {d.FilePath}");
        }

        var compilation = await project.GetCompilationAsync();
        Assert.NotNull(compilation);

        // Even if workspace docs are empty, direct driver on Adhoc compilation is the fallback seam.
        var generators = analyzerRefs
            .SelectMany(r => r.GetGenerators(LanguageNames.CSharp))
            .ToImmutableArray();

        // Adhoc compilation may already include generated trees if workspace ran them.
        var adhocGenerated = docs.OfType<SourceGeneratedDocument>().ToImmutableArray();
        Compilation baseComp = compilation!;
        if (adhocGenerated.Length > 0)
        {
            baseComp = AttributionHelpers.StripGeneratedTrees(compilation!, adhocGenerated);
        }

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators,
            parseOptions: (CSharpParseOptions)parseOptions,
            optionsProvider: project.AnalyzerOptions.AnalyzerConfigOptionsProvider);
        driver = driver.RunGenerators(baseComp);
        var run = driver.GetRunResult();
        var sourceCount = run.Results.Sum(r => r.GeneratedSources.Length);
        _output.WriteLine($"Direct driver on Adhoc base compilation: {sourceCount} sources; generators={generators.Length}");

        Assert.True(generators.Length > 0, "Expected AnalyzerReferences to expose generators.");
        Assert.True(sourceCount > 0 || docs.Length > 0,
            "Either AdhocWorkspace materializes generated docs or direct driver must produce sources.");

        if (docs.Length == 0)
        {
            _output.WriteLine("FINDING: AdhocWorkspace GetSourceGeneratedDocumentsAsync returned 0 — unit tests should drive GeneratorDriver directly.");
        }
        else
        {
            _output.WriteLine("FINDING: AdhocWorkspace DID materialize source-generated documents.");
        }
    }
}
