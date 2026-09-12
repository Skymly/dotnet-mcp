using System.Collections.Immutable;
using DotNetMcp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

public class GeneratorExceptionTests
{
    [Fact]
    public async Task source_output_exception_is_mcpgen0001_and_sibling_still_emits()
    {
        using var workspace = CreateWorkspace(
            new ThrowingSourceOutputGenerator(),
            new SiblingMarkerGenerator());
        var projectId = workspace.CurrentSolution.Projects.Single().Id.Id.ToString("D");
        var service = new GeneratorQueryService();
        var cache = new GeneratorRunCache();

        using var session = new FakeSession(workspace.CurrentSolution, epoch: 1, cache);
        var (page, error) = await service.ListGeneratorDiagnosticsAsync(
            session,
            projectId,
            assemblyName: typeof(ThrowingSourceOutputGenerator).Assembly.GetName().Name!,
            typeFullName: typeof(ThrowingSourceOutputGenerator).FullName!);

        Assert.Null(error);
        Assert.NotNull(page);
        var item = Assert.Single(
            page!.Page.Items,
            d => d.Id == GeneratorDriverRunner.GeneratorExceptionDiagnosticId);
        Assert.Equal(nameof(DiagnosticSeverity.Error), item.Severity);
        Assert.Contains(typeof(InvalidOperationException).FullName!, item.Message, StringComparison.Ordinal);
        Assert.Contains(ThrowingSourceOutputGenerator.Boom, item.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("at DotNetMcp.Tests", item.Message, StringComparison.Ordinal);

        var (sources, sourceError) = await service.ListGeneratedSourcesAsync(
            session,
            projectId,
            assemblyName: typeof(SiblingMarkerGenerator).Assembly.GetName().Name!,
            typeFullName: typeof(SiblingMarkerGenerator).FullName!);
        Assert.Null(sourceError);
        Assert.Contains(sources!.Items, s => s.HintName == SiblingMarkerGenerator.HintName);

        Assert.True(cache.TryGet(workspace.CurrentSolution.Projects.Single().Id.Id.ToString("D"), 1, out _));
    }

    [Fact]
    public async Task driver_failure_is_compilation_unavailable_and_is_not_cached()
    {
        using var workspace = CreateWorkspace(InMemoryGeneratorReference.ThrowingOnGetGenerators(
            new InvalidOperationException("driver-boom")));
        var projectId = workspace.CurrentSolution.Projects.Single().Id.Id.ToString("D");
        var service = new GeneratorQueryService();
        var cache = new GeneratorRunCache();

        using var session = new FakeSession(workspace.CurrentSolution, epoch: 1, cache);
        var (snapshot, error) = await service.GetDriverRunAsync(session, projectId);

        Assert.Null(snapshot);
        Assert.IsType<CompilationUnavailableError>(error);
        Assert.False(string.IsNullOrWhiteSpace(error!.Message));
        Assert.False(cache.TryGet(projectId, 1, out _));
    }

    private static AdhocWorkspace CreateWorkspace(params IIncrementalGenerator[] generators) =>
        CreateWorkspace(new InMemoryGeneratorReference(generators));

    private static AdhocWorkspace CreateWorkspace(AnalyzerReference analyzer)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        const string projectFilePath = @"C:\fake\GeneratorExceptionHost.csproj";

        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "GeneratorExceptionHost",
            "GeneratorExceptionHost",
            LanguageNames.CSharp,
            filePath: projectFilePath));

        solution = solution.AddDocument(
            docId,
            "Host.cs",
            SourceText.From("namespace Host; public static class H { }"),
            filePath: Path.Combine(Path.GetDirectoryName(projectFilePath)!, "Host.cs"));
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(
            projectId,
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        solution = solution.AddAnalyzerReference(projectId, analyzer);

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace.");
        }

        return workspace;
    }

    private sealed class ThrowingSourceOutputGenerator : IIncrementalGenerator
    {
        public const string Boom = "boom-from-source-output";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterSourceOutput(
                context.CompilationProvider,
                static (_, _) => throw new InvalidOperationException(Boom));
        }
    }

    private sealed class SiblingMarkerGenerator : IIncrementalGenerator
    {
        public const string HintName = "Sibling.g.cs";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterPostInitializationOutput(ctx =>
                ctx.AddSource(HintName, "internal static class SiblingMarker {}"));
        }
    }

    private sealed class InMemoryGeneratorReference : AnalyzerReference
    {
        private readonly ImmutableArray<ISourceGenerator> _generators;
        private readonly Exception? _getGeneratorsThrows;

        public InMemoryGeneratorReference(params IIncrementalGenerator[] generators)
            : this(generators.Select(g => g.AsSourceGenerator()).ToImmutableArray(), null)
        {
        }

        private InMemoryGeneratorReference(
            ImmutableArray<ISourceGenerator> generators,
            Exception? getGeneratorsThrows)
        {
            _generators = generators;
            _getGeneratorsThrows = getGeneratorsThrows;
        }

        public static InMemoryGeneratorReference ThrowingOnGetGenerators(Exception ex) =>
            new(ImmutableArray<ISourceGenerator>.Empty, ex);

        public override string Display => "test-generators";

        public override string FullPath => @"C:\fake\test-generators.dll";

        public override object Id => Display;

        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() =>
            ImmutableArray<DiagnosticAnalyzer>.Empty;

        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) =>
            ImmutableArray<DiagnosticAnalyzer>.Empty;

        public override ImmutableArray<ISourceGenerator> GetGenerators(string language)
        {
            if (_getGeneratorsThrows is not null)
            {
                throw _getGeneratorsThrows;
            }

            return _generators;
        }
    }

    private sealed class FakeSession : IWorkspaceSession
    {
        private readonly GeneratorRunCache _cache;

        public FakeSession(Solution solution, long epoch, GeneratorRunCache cache)
        {
            Solution = solution;
            Epoch = epoch;
            FSharpSnapshot = new FSharpWorkspaceSnapshot(epoch, []);
            _cache = cache;
        }

        public long Epoch { get; }

        public Solution Solution { get; }

        public FSharpWorkspaceSnapshot FSharpSnapshot { get; }

        public async Task<Compilation> GetCompilationAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default)
        {
            var project = Solution.GetProject(projectId)
                ?? throw new InvalidOperationException($"Project '{projectId.Id}' is not in the session solution.");
            return await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Compilation was null for project '{project.Name}'.");
        }

        public async Task<Compilation> GetCompilationWithoutGeneratedTreesAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default)
        {
            var project = Solution.GetProject(projectId)
                ?? throw new InvalidOperationException($"Project '{projectId.Id}' is not in the session solution.");
            var full = await GetCompilationAsync(projectId, cancellationToken).ConfigureAwait(false);
            return await GeneratorDriverRunner
                .StripGeneratedTreesFromProjectAsync(project, full, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<DriverRunSnapshot> GetGeneratorRunResultAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default)
        {
            var key = projectId.Id.ToString("D");
            if (_cache.TryGet(key, Epoch, out var cached))
            {
                return cached;
            }

            var project = Solution.GetProject(projectId)
                ?? throw new InvalidOperationException($"Project '{projectId.Id}' is not in the session solution.");
            var baseCompilation = await GetCompilationWithoutGeneratedTreesAsync(projectId, cancellationToken)
                .ConfigureAwait(false);
            var snapshot = GeneratorDriverRunner.RunDriver(project, baseCompilation, cancellationToken);
            _cache.Set(key, Epoch, snapshot);
            return snapshot;
        }

        public void Dispose()
        {
        }
    }
}
