using DotNetMcp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

public class CodeRefactoringServiceTests
{
    [Fact]
    public async Task list_async_returns_actions_for_handwritten_field()
    {
        using var workspace = CreateFieldWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var (service, handle) = await ServiceAndFieldHandleAsync(session);

        var (listed, error) = await service.ListAsync(session, handle);
        Assert.Null(error);
        Assert.NotNull(listed);
        Assert.NotEmpty(listed!.Items);
    }

    [Fact]
    public async Task list_async_generated_origin_is_refused()
    {
        using var workspace = CreateGeneratorWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var roslyn = new RoslynLanguageAdapter(new GeneratorQueryService());
        var service = new CodeRefactoringService(new LanguageAdapters([roslyn]), roslyn);
        var (resolved, resolveError) = await roslyn.ResolveByNameAsync(session, "CustomMarker");
        Assert.Null(resolveError);

        var (_, error) = await service.ListAsync(session, resolved!.Handle);
        Assert.IsType<GeneratedSymbolRefactoringRefusedError>(error);
    }

    [Fact]
    public async Task list_async_unparseable_handle_is_invalid()
    {
        using var workspace = CreateFieldWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var (service, _) = await ServiceAndFieldHandleAsync(session);
        var (_, error) = await service.ListAsync(session, "");
        Assert.IsType<InvalidSymbolHandleError>(error);
    }

    [Fact]
    public async Task list_async_unsupported_language_is_refactoring_language_not_supported()
    {
        using var workspace = CreateFieldWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var roslyn = new RoslynLanguageAdapter(new GeneratorQueryService());
        var fake = new FakeAdapter();
        var service = new CodeRefactoringService(new LanguageAdapters([fake, roslyn]), roslyn);
        var (resolved, _) = await roslyn.ResolveByNameAsync(session, "count");

        var (_, error) = await service.ListAsync(session, resolved!.Handle);
        Assert.IsType<RefactoringLanguageNotSupportedError>(error);
    }

    [Fact]
    public async Task list_async_missing_symbol_is_not_found()
    {
        using var workspace = CreateFieldWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var (service, _) = await ServiceAndFieldHandleAsync(session);
        var handle = SymbolHandle.Create("csharp", ProjectIdOf(workspace), "Lib.Gone").Format();
        var (_, error) = await service.ListAsync(session, handle);
        Assert.IsType<SymbolNotFoundError>(error);
    }

    [Fact]
    public async Task build_preview_returns_handwritten_draft()
    {
        using var workspace = CreateFieldWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var (service, handle) = await ServiceAndFieldHandleAsync(session);
        var (listed, listError) = await service.ListAsync(session, handle);
        Assert.Null(listError);

        var (draft, error) = await service.BuildPreviewAsync(session, handle, listed!.Items[0].RefactoringIndex);
        Assert.Null(error);
        Assert.NotNull(draft);
        Assert.NotEmpty(draft!.Documents);
        Assert.Contains(draft.Documents, s => s.NewText != s.OldText);
    }

    [Fact]
    public async Task build_preview_out_of_range_index_is_refactoring_index_out_of_range()
    {
        using var workspace = CreateFieldWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var (service, handle) = await ServiceAndFieldHandleAsync(session);
        var (_, error) = await service.BuildPreviewAsync(session, handle, 99);
        Assert.IsType<RefactoringIndexOutOfRangeError>(error);
    }

    [Fact]
    public async Task build_preview_unparseable_handle_is_invalid()
    {
        using var workspace = CreateFieldWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var (service, _) = await ServiceAndFieldHandleAsync(session);
        var (_, error) = await service.BuildPreviewAsync(session, "", 0);
        Assert.IsType<InvalidSymbolHandleError>(error);
    }

    [Fact]
    public async Task build_preview_unsupported_language_is_refactoring_language_not_supported()
    {
        using var workspace = CreateFieldWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var roslyn = new RoslynLanguageAdapter(new GeneratorQueryService());
        var service = new CodeRefactoringService(new LanguageAdapters([new FakeAdapter(), roslyn]), roslyn);
        var (resolved, _) = await roslyn.ResolveByNameAsync(session, "count");
        var (_, error) = await service.BuildPreviewAsync(session, resolved!.Handle, 0);
        Assert.IsType<RefactoringLanguageNotSupportedError>(error);
    }

    [Fact]
    public async Task build_preview_generated_origin_is_refused()
    {
        using var workspace = CreateGeneratorWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var roslyn = new RoslynLanguageAdapter(new GeneratorQueryService());
        var service = new CodeRefactoringService(new LanguageAdapters([roslyn]), roslyn);
        var (resolved, _) = await roslyn.ResolveByNameAsync(session, "CustomMarker");
        var (_, error) = await service.BuildPreviewAsync(session, resolved!.Handle, 0);
        Assert.IsType<GeneratedSymbolRefactoringRefusedError>(error);
    }

    [Fact]
    public async Task build_preview_missing_symbol_is_not_found()
    {
        using var workspace = CreateFieldWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var (service, _) = await ServiceAndFieldHandleAsync(session);
        var handle = SymbolHandle.Create("csharp", ProjectIdOf(workspace), "Lib.Gone").Format();
        var (_, error) = await service.BuildPreviewAsync(session, handle, 0);
        Assert.IsType<SymbolNotFoundError>(error);
    }
    private static async Task<(CodeRefactoringService Service, string Handle)> ServiceAndFieldHandleAsync(
        IWorkspaceSession session)
    {
        var roslyn = new RoslynLanguageAdapter(new GeneratorQueryService());
        var service = new CodeRefactoringService(new LanguageAdapters([roslyn]), roslyn);
        var (resolved, error) = await roslyn.ResolveByNameAsync(session, "count");
        Assert.Null(error);
        Assert.NotNull(resolved);
        return (service, resolved!.Handle);
    }

    private static string ProjectIdOf(AdhocWorkspace workspace) =>
        workspace.CurrentSolution.Projects.Single().Id.Id.ToString("D");

    private static AdhocWorkspace CreateFieldWorkspace()
    {
        const string source = """
            namespace Lib;
            public sealed class Widget
            {
                public int count;
            }
            """;
        return CreateWorkspace("Lib", source, @"C:\fake\Widget.cs", withGenerator: false);
    }

    private static AdhocWorkspace CreateGeneratorWorkspace()
    {
        const string source = """
            namespace GeneratorHost;
            public static class Host { public static string Name => "host"; }
            public partial class PartialThing { public string Format() => "hw"; }
            """;
        return CreateWorkspace("GeneratorHost", source, @"C:\fake\Host.cs", withGenerator: true);
    }

    private static AdhocWorkspace CreateWorkspace(string name, string source, string filePath, bool withGenerator)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            name,
            name,
            LanguageNames.CSharp,
            filePath: $@"C:\fake\{name}.csproj"));
        solution = solution.AddDocument(
            DocumentId.CreateNewId(projectId),
            Path.GetFileName(filePath),
            SourceText.From(source),
            filePath: filePath);
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(
            projectId,
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        if (withGenerator)
        {
            solution = solution.AddAnalyzerReference(
                projectId,
                new AnalyzerFileReference(
                    typeof(CustomGenerator.MarkerGenerator).Assembly.Location,
                    AnalyzerAssemblyLoader.Instance));
        }

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace.");
        }

        return workspace;
    }

    private sealed class FakeSession : IWorkspaceSession
    {
        private readonly GeneratorRunCache _cache = new();

        public FakeSession(Solution solution)
        {
            Solution = solution;
            Epoch = 1;
            FSharpSnapshot = new FSharpWorkspaceSnapshot(1, []);
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

    private sealed class FakeAdapter : ILanguageAdapter
    {
        public bool OwnsLanguage(string languageToken) =>
            string.Equals(languageToken, LanguageAdapters.CSharpLanguage, StringComparison.OrdinalIgnoreCase);

        public bool OwnsProject(Project project) => project.Language == LanguageNames.CSharp;

        public bool SupportsCodeRefactoring => false;

        public bool SupportsDiagnosticFix => false;

        public Task<(SymbolResolveSuccess? Success, SymbolQueryError? Error)> ResolveByNameAsync(
            IWorkspaceSession session, string name, string? projectId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(SymbolResolveSuccess? Success, SymbolQueryError? Error)> GetSummaryAsync(
            IWorkspaceSession session, string handle, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(SymbolDefinitionSuccess? Success, SymbolQueryError? Error)> GetDefinitionAsync(
            IWorkspaceSession session, string handle, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(PagedResult<MemberListItem>? Success, SymbolQueryError? Error)> GetMembersAsync(
            IWorkspaceSession session, string handle, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(PagedResult<ReferenceLocationItem>? Success, SymbolQueryError? Error)> FindReferencesAsync(
            IWorkspaceSession session, string handle, bool entireSolution = false, int? limit = null, string? cursor = null, TimeSpan? softBudget = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(PagedResult<ImplementationItem>? Success, SymbolQueryError? Error)> FindImplementationsAsync(
            IWorkspaceSession session, string handle, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(PagedResult<HierarchyItem>? Success, SymbolQueryError? Error)> GetTypeHierarchyAsync(
            IWorkspaceSession session, string handle, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(PagedResult<CallerLocationItem>? Success, SymbolQueryError? Error)> FindCallersAsync(
            IWorkspaceSession session, string handle, int? limit = null, string? cursor = null, TimeSpan? softBudget = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(PagedResult<DiagnosticItem>? Success, SymbolQueryError? Error)> GetProjectDiagnosticsAsync(
            IWorkspaceSession session, string projectId, int? limit = null, string? cursor = null, TimeSpan? softBudget = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(RenamePreviewDraft? Draft, SymbolQueryError? Error)> BuildRenamePreviewAsync(
            IWorkspaceSession session, string handle, string newName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
