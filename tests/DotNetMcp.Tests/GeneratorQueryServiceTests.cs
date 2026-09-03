using DotNetMcp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

public class GeneratorQueryServiceTests
{
    [Fact]
    public async Task list_generators_caches_by_project_id_and_epoch()
    {
        using var workspace = CreateWorkspace();
        var projectId = workspace.CurrentSolution.Projects.Single().Id.Id.ToString("D");
        var service = new GeneratorQueryService();

        using var session7 = new FakeSession(workspace.CurrentSolution, epoch: 7);
        var (first, error1) = await service.ListGeneratorsAsync(session7, projectId);
        Assert.Null(error1);
        Assert.NotNull(first);

        using var session7b = new FakeSession(workspace.CurrentSolution, epoch: 7);
        var (second, error2) = await service.ListGeneratorsAsync(session7b, projectId);
        Assert.Null(error2);
        Assert.Same(first, second);

        using var session8 = new FakeSession(workspace.CurrentSolution, epoch: 8);
        var (third, error3) = await service.ListGeneratorsAsync(session8, projectId);
        Assert.Null(error3);
        Assert.NotSame(first, third);
        Assert.Equal(first!.Count, third!.Count);
    }

    [Fact]
    public async Task list_generated_sources_and_driver_cache_share_epoch_key()
    {
        using var workspace = CreateWorkspace();
        var projectId = workspace.CurrentSolution.Projects.Single().Id.Id.ToString("D");
        var service = new GeneratorQueryService();
        var cache = new GeneratorRunCache();

        using var session3 = new FakeSession(workspace.CurrentSolution, epoch: 3, cache);
        var (page1, error1) = await service.ListGeneratedSourcesAsync(
            session3,
            projectId,
            assemblyName: "CustomGenerator",
            typeFullName: "CustomGenerator.MarkerGenerator");
        Assert.Null(error1);
        Assert.NotNull(page1);
        Assert.Contains(page1!.Items, i => i.HintName == CustomGenerator.MarkerGenerator.HintName);
        Assert.NotEmpty(page1.Items);
        Assert.False(page1.Truncated);

        var (snap1, snapError1) = await service.GetDriverRunAsync(session3, projectId);
        Assert.Null(snapError1);
        Assert.NotNull(snap1);

        using var session3b = new FakeSession(workspace.CurrentSolution, epoch: 3, cache);
        var (snap2, snapError2) = await service.GetDriverRunAsync(session3b, projectId);
        Assert.Null(snapError2);
        Assert.Same(snap1, snap2);

        using var session4 = new FakeSession(workspace.CurrentSolution, epoch: 4, cache);
        var (snap3, snapError3) = await service.GetDriverRunAsync(session4, projectId);
        Assert.Null(snapError3);
        Assert.NotSame(snap1, snap3);
    }

    [Fact]
    public async Task list_generator_diagnostics_returns_fixture_diagnostic()
    {
        using var workspace = CreateWorkspace();
        var projectId = workspace.CurrentSolution.Projects.Single().Id.Id.ToString("D");
        var service = new GeneratorQueryService();

        using var session = new FakeSession(workspace.CurrentSolution, epoch: 1);
        var (page, error) = await service.ListGeneratorDiagnosticsAsync(
            session,
            projectId,
            assemblyName: "CustomGenerator",
            typeFullName: "CustomGenerator.DiagnosticEmittingGenerator");

        Assert.Null(error);
        Assert.NotNull(page);
        Assert.Equal("CustomGenerator", page!.Identity.AssemblyName);
        Assert.Equal("CustomGenerator.DiagnosticEmittingGenerator", page.Identity.TypeFullName);
        var item = Assert.Single(page.Page.Items);
        Assert.Equal(CustomGenerator.DiagnosticEmittingGenerator.DiagnosticId, item.Id);
        Assert.Equal(nameof(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning), item.Severity);
        Assert.Equal(CustomGenerator.DiagnosticEmittingGenerator.DiagnosticMessage, item.Message);
        Assert.False(page.Page.Truncated);
    }

    [Fact]
    public void generator_run_cache_hits_same_project_and_epoch()
    {
        var cache = new GeneratorRunCache();
        var snapshot = new DriverRunSnapshot([], []);
        cache.Set("proj", 3, snapshot);

        Assert.True(cache.TryGet("proj", 3, out var hit));
        Assert.Same(snapshot, hit);
        Assert.False(cache.TryGet("proj", 4, out _));
    }

    [Fact]
    public async Task list_generators_missing_project_is_project_not_found()
    {
        using var workspace = CreateWorkspace();
        var service = new GeneratorQueryService();
        using var session = new FakeSession(workspace.CurrentSolution, epoch: 1);

        var (_, error) = await service.ListGeneratorsAsync(session, "missing-project");

        Assert.IsType<ProjectNotFoundError>(error);
    }

    [Fact]
    public async Task list_generated_sources_unknown_generator_is_generator_not_found()
    {
        using var workspace = CreateWorkspace();
        var projectId = workspace.CurrentSolution.Projects.Single().Id.Id.ToString("D");
        var service = new GeneratorQueryService();
        using var session = new FakeSession(workspace.CurrentSolution, epoch: 1);

        var (_, error) = await service.ListGeneratedSourcesAsync(
            session,
            projectId,
            assemblyName: "NoSuchAssembly",
            typeFullName: "NoSuch.Generator");

        Assert.IsType<GeneratorNotFoundError>(error);
    }

    [Fact]
    public async Task match_syntax_tree_identical_content_from_two_generators_is_ambiguous()
    {
        using var workspace = CreateWorkspace();
        var projectId = workspace.CurrentSolution.Projects.Single().Id.Id.ToString("D");
        const string shared = "class Shared {}";
        var snapshot = new DriverRunSnapshot(
            [],
            [
                new GeneratedSourceMatch(
                    new GeneratorIdentity("Alpha", "Alpha.Gen", "1.0.0.0"),
                    "A.g.cs",
                    shared,
                    CSharpSyntaxTree.ParseText(shared, path: "A.g.cs")),
                new GeneratedSourceMatch(
                    new GeneratorIdentity("Beta", "Beta.Gen", "1.0.0.0"),
                    "B.g.cs",
                    shared,
                    CSharpSyntaxTree.ParseText(shared, path: "B.g.cs"))
            ]);
        var service = new GeneratorQueryService();
        using var session = new FakeSession(workspace.CurrentSolution, epoch: 1, snapshot: snapshot);

        var (_, error) = await service.MatchSyntaxTreeAsync(
            session,
            projectId,
            CSharpSyntaxTree.ParseText(shared, path: "query.cs"));

        Assert.IsType<GeneratorAttributionAmbiguousError>(error);
    }

    [Fact]
    public async Task get_driver_run_unavailable_compilation_is_compilation_unavailable()
    {
        using var workspace = CreateWorkspace();
        var projectId = workspace.CurrentSolution.Projects.Single().Id.Id.ToString("D");
        var service = new GeneratorQueryService();
        using var session = new FakeSession(
            workspace.CurrentSolution,
            epoch: 1,
            compilationUnavailable: true);

        var (_, error) = await service.GetDriverRunAsync(session, projectId);

        Assert.IsType<CompilationUnavailableError>(error);
    }

    [Fact]
    public async Task list_generated_sources_bad_cursor_is_stale()
    {
        using var workspace = CreateWorkspace();
        var projectId = workspace.CurrentSolution.Projects.Single().Id.Id.ToString("D");
        var service = new GeneratorQueryService();
        using var session = new FakeSession(workspace.CurrentSolution, epoch: 1);

        var (_, error) = await service.ListGeneratedSourcesAsync(
            session,
            projectId,
            assemblyName: "CustomGenerator",
            typeFullName: "CustomGenerator.MarkerGenerator",
            cursor: "not-a-cursor");

        Assert.IsType<StaleCursorError>(error);
    }

    [Fact]
    public async Task list_generated_sources_wrong_epoch_cursor_is_stale()
    {
        using var workspace = CreateWorkspace();
        var projectId = workspace.CurrentSolution.Projects.Single().Id.Id.ToString("D");
        var service = new GeneratorQueryService();
        using var session = new FakeSession(workspace.CurrentSolution, epoch: 1);
        var cursor = GeneratedSourcesPageCursor.Encode(
            epoch: 99,
            assemblyName: "CustomGenerator",
            typeFullName: "CustomGenerator.MarkerGenerator",
            offset: 0);

        var (_, error) = await service.ListGeneratedSourcesAsync(
            session,
            projectId,
            assemblyName: "CustomGenerator",
            typeFullName: "CustomGenerator.MarkerGenerator",
            cursor: cursor);

        Assert.IsType<StaleCursorError>(error);
    }

    private static AdhocWorkspace CreateWorkspace()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        const string projectFilePath = @"C:\fake\GeneratorHost.csproj";

        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "GeneratorHost",
            "GeneratorHost",
            LanguageNames.CSharp,
            filePath: projectFilePath));

        const string source = """
            namespace GeneratorHost;

            public static class Host
            {
                public static string Name => "host";
            }

            public partial class PartialThing
            {
                public string Format() => "hw";
                public string Format(string x) => x;
            }
            """;

        var projectDir = Path.GetDirectoryName(projectFilePath) ?? @"C:\fake";
        solution = solution.AddDocument(
            docId,
            "Host.cs",
            SourceText.From(source),
            filePath: Path.Combine(projectDir, "Host.cs"));
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(
            projectId,
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        solution = solution.AddAnalyzerReference(
            projectId,
            new AnalyzerFileReference(
                typeof(CustomGenerator.MarkerGenerator).Assembly.Location,
                AnalyzerAssemblyLoader.Instance));

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace.");
        }

        return workspace;
    }

    private sealed class FakeSession : IWorkspaceSession
    {
        private readonly GeneratorRunCache _cache;
        private readonly bool _compilationUnavailable;
        private readonly DriverRunSnapshot? _snapshot;

        public FakeSession(
            Solution solution,
            long epoch,
            GeneratorRunCache? cache = null,
            bool compilationUnavailable = false,
            DriverRunSnapshot? snapshot = null)
        {
            Solution = solution;
            Epoch = epoch;
            FSharpSnapshot = new FSharpWorkspaceSnapshot(epoch, []);
            _cache = cache ?? new GeneratorRunCache();
            _compilationUnavailable = compilationUnavailable;
            _snapshot = snapshot;
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
            if (_compilationUnavailable)
            {
                throw new InvalidOperationException("Compilation was null.");
            }

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
            if (_snapshot is not null)
            {
                return _snapshot;
            }

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

    private sealed class AnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
    {
        public static AnalyzerAssemblyLoader Instance { get; } = new();

        public void AddDependencyLocation(string fullPath)
        {
        }

        public System.Reflection.Assembly LoadFromPath(string fullPath) =>
            System.Reflection.Assembly.LoadFrom(fullPath);
    }
}
