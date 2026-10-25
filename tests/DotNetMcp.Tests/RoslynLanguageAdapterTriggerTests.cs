using DotNetMcp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

/// <summary>
/// Unit-level seam tests for generation triggers (issue #524): convention-rule
/// degradation paths that cannot be produced honestly by a real generator run
/// (zero candidates, ambiguous candidates, unknown generator identities).
/// </summary>
public class RoslynLanguageAdapterTriggerTests
{
    private const string ConventionSource = """
        using System;

        namespace Amb
        {
            [AttributeUsage(AttributeTargets.Field)]
            public sealed class ObservablePropertyAttribute : Attribute
            {
            }

            public class ViewModel
            {
                // Two attribute carriers for the same property name: any unique-match
                // convention rule must degrade rather than pick one.
                [ObservableProperty]
                private int _name;

                [ObservableProperty]
                private int m_name;

                public int Name => 0;

                // RelayCommand-shaped name with no [RelayCommand] method in the type.
                public int SaveCommand => 0;
            }
        }
        """;

    private static RoslynLanguageAdapter Adapter() => new(new GeneratorQueryService());

    private static async Task<(Project Project, INamedTypeSymbol Type)> LoadTypeAsync(
        FakeSession session, string typeName)
    {
        var project = session.Solution.Projects.Single();
        var compilation = await project.GetCompilationAsync();
        var type = compilation!.GetTypeByMetadataName(typeName)
            ?? throw new InvalidOperationException($"Type '{typeName}' not found.");
        return (project, type);
    }

    [Fact]
    public async Task unknown_generator_reports_message_not_guess()
    {
        using var session = CreateSession(ConventionSource);
        var (project, type) = await LoadTypeAsync(session, "Amb.ViewModel");
        var symbol = type.GetMembers("Name").Single();

        var (triggers, message) = await Adapter().ResolveTriggersAsync(
            session, project, symbol,
            new GeneratorIdentity("Acme.Generators", "Acme.Gen", "1.0"), CancellationToken.None);

        Assert.Empty(triggers);
        Assert.NotNull(message);
        Assert.Contains("Acme.Generators::Acme.Gen", message, StringComparison.Ordinal);
        Assert.Contains("No trigger rule", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task known_rule_without_candidates_reports_known_rule_miss()
    {
        using var session = CreateSession(ConventionSource);
        var (project, type) = await LoadTypeAsync(session, "Amb.ViewModel");
        var symbol = type.GetMembers("SaveCommand").Single();

        var (triggers, message) = await Adapter().ResolveTriggersAsync(
            session, project, symbol,
            new GeneratorIdentity(
                "CommunityToolkit.Mvvm.SourceGenerators",
                "CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator",
                "8.4.0.0"),
            CancellationToken.None);

        Assert.Empty(triggers);
        Assert.NotNull(message);
        Assert.Contains("no unique trigger", message, StringComparison.Ordinal);
        Assert.DoesNotContain("No trigger rule", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ambiguous_convention_candidates_degrade_honestly()
    {
        using var session = CreateSession(ConventionSource);
        var (project, type) = await LoadTypeAsync(session, "Amb.ViewModel");
        var symbol = type.GetMembers("Name").Single();

        var (triggers, message) = await Adapter().ResolveTriggersAsync(
            session, project, symbol,
            new GeneratorIdentity(
                "CommunityToolkit.Mvvm.SourceGenerators",
                "CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator",
                "8.4.0.0"),
            CancellationToken.None);

        Assert.Empty(triggers);
        Assert.NotNull(message);
        Assert.Contains("ambiguous", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("_name", message, StringComparison.Ordinal);
        Assert.Contains("m_name", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task unique_convention_candidate_reports_attribute_trigger()
    {
        const string source = """
            using System;

            namespace Amb
            {
                [AttributeUsage(AttributeTargets.Field)]
                public sealed class ObservablePropertyAttribute : Attribute
                {
                }

                public class ViewModel
                {
                    [ObservableProperty]
                    private int _name;

                    public int Name => 0;
                }
            }
            """;
        using var session = CreateSession(source);
        var (project, type) = await LoadTypeAsync(session, "Amb.ViewModel");
        var symbol = type.GetMembers("Name").Single();

        var (triggers, message) = await Adapter().ResolveTriggersAsync(
            session, project, symbol,
            new GeneratorIdentity(
                "CommunityToolkit.Mvvm.SourceGenerators",
                "CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator",
                "8.4.0.0"),
            CancellationToken.None);

        var trigger = Assert.Single(triggers);
        Assert.Null(message);
        Assert.Equal(GenerationTriggerKind.Attribute, trigger.Kind);
        Assert.Equal(GenerationTriggerConfidence.Convention, trigger.Confidence);
        Assert.Equal("ObservableProperty", trigger.Detail);
        Assert.NotNull(trigger.Handle);
        Assert.True(SymbolHandle.TryParse(trigger.Handle!, out _, out _));
        // The trigger span is the attribute syntax node itself.
        var root = await session.Solution.Projects.Single().Documents.Single()
            .GetSyntaxRootAsync();
        var node = root!.FindNode(new TextSpan(trigger.Start!.Value, trigger.Length!.Value));
        Assert.Equal("ObservableProperty", node.ToString());
    }

    [Fact]
    public async Task generated_implementation_part_pairs_to_handwritten_definition()
    {
        // The GeneratedRegex shape: the handwritten member surfaced by GetMembers is
        // the definition; the generated implementation part is reachable through
        // IMethodSymbol.PartialImplementationPart and resolves its trigger to the
        // handwritten definition (S9 rule table — RegexGenerator / fallback pairing).
        await using var loaded = FakeSolutionLoader.CreateGeneratorsLoaded();
        using var session = new FakeSession((AdhocWorkspace)loaded.Workspace);
        var project = session.Solution.Projects.Single();
        var compilation = await session.GetCompilationAsync(project.Id);
        var type = compilation!.GetTypeByMetadataName("GeneratorHost.PartialThing")
            ?? throw new InvalidOperationException("PartialThing not found.");
        var definition = (IMethodSymbol)type.GetMembers("GeneratedImpl").Single();
        var implementation = definition.PartialImplementationPart
            ?? throw new InvalidOperationException("Expected a generated implementation part.");

        var (attribution, error) = await Adapter().AttributeSymbolAsync(
            session, project, implementation, CancellationToken.None);

        Assert.Null(error);
        Assert.NotNull(attribution);
        Assert.Equal("SourceGenerator", attribution!.OriginKind);
        Assert.Equal(
            "CustomGenerator.PartialAugmentGenerator", attribution.Generator!.TypeFullName);
        var trigger = Assert.Single(attribution.Triggers);
        Assert.Equal(GenerationTriggerKind.PartialDefinition, trigger.Kind);
        Assert.Equal(GenerationTriggerConfidence.Exact, trigger.Confidence);
        Assert.Equal("partial definition", trigger.Detail);
        Assert.Equal("GeneratedImpl".Length, trigger.Length);
        Assert.EndsWith("Host.cs", trigger.Path, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(trigger.Handle);
        Assert.True(SymbolHandle.TryParse(trigger.Handle!, out var parsed, out _));
        Assert.Equal("GeneratorHost.PartialThing.GeneratedImpl()", parsed!.SignatureQualifiedName);
    }

    private static FakeSession CreateSession(string source)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "AmbHost",
            "AmbHost",
            LanguageNames.CSharp,
            filePath: @"C:\fake\AmbHost.csproj"));
        solution = solution.AddDocument(
            docId, "Host.cs", SourceText.From(source), filePath: @"C:\fake\Host.cs");
        solution = solution.WithProjectCompilationOptions(
            projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(
            projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply trigger-rule AdhocWorkspace.");
        }

        return new FakeSession(workspace);
    }

    private sealed class FakeSession : IWorkspaceSession
    {
        private readonly AdhocWorkspace _workspace;
        private readonly GeneratorRunCache _cache = new();

        public FakeSession(AdhocWorkspace workspace)
        {
            _workspace = workspace;
            Solution = workspace.CurrentSolution;
            Epoch = 1;
            FSharpSnapshot = new FSharpWorkspaceSnapshot(1, []);
        }

        public long Epoch { get; }

        public Solution Solution { get; }

        public FSharpWorkspaceSnapshot FSharpSnapshot { get; }

        public async Task<Compilation> GetCompilationAsync(
            ProjectId projectId, CancellationToken cancellationToken = default) =>
            await Solution.GetProject(projectId)!.GetCompilationAsync(cancellationToken)
                ?? throw new InvalidOperationException("Null compilation.");

        public async Task<Compilation> GetCompilationWithoutGeneratedTreesAsync(
            ProjectId projectId, CancellationToken cancellationToken = default)
        {
            var project = Solution.GetProject(projectId)
                ?? throw new InvalidOperationException($"Project '{projectId.Id}' is not in the session solution.");
            var full = await GetCompilationAsync(projectId, cancellationToken);
            return await GeneratorDriverRunner
                .StripGeneratedTreesFromProjectAsync(project, full, cancellationToken);
        }

        public async Task<DriverRunSnapshot> GetGeneratorRunResultAsync(
            ProjectId projectId, CancellationToken cancellationToken = default)
        {
            var key = projectId.Id.ToString("D");
            if (_cache.TryGet(key, Epoch, out var cached))
            {
                return cached;
            }

            var project = Solution.GetProject(projectId)
                ?? throw new InvalidOperationException($"Project '{projectId.Id}' is not in the session solution.");
            var baseCompilation = await GetCompilationWithoutGeneratedTreesAsync(projectId, cancellationToken);
            var snapshot = GeneratorDriverRunner.RunDriver(project, baseCompilation, cancellationToken);
            _cache.Set(key, Epoch, snapshot);
            return snapshot;
        }

        public void Dispose() => _workspace.Dispose();
    }
}
