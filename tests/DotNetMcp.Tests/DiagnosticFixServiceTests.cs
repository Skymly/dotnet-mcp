using DotNetMcp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

public class DiagnosticFixServiceTests
{
    [Fact]
    public async Task list_fixes_returns_actions_for_handwritten_missing_using()
    {
        using var workspace = CreateMissingUsingWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var projectId = ProjectIdOf(workspace);
        var (listed, error) = await new DiagnosticFixService().ListFixesAsync(
            session, projectId, "CS0246", @"C:\fake\Broken.cs", null, null, null, null);

        Assert.Null(error);
        Assert.NotNull(listed);
        Assert.NotEmpty(listed!.Items);
    }

    [Fact]
    public async Task list_fixes_unknown_project_is_not_found()
    {
        using var workspace = CreateMissingUsingWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var (_, error) = await new DiagnosticFixService().ListFixesAsync(
            session, "missing", "CS0246", null, null, null, null, null);
        Assert.IsType<ProjectNotFoundError>(error);
    }

    [Fact]
    public async Task list_fixes_unknown_diagnostic_is_not_found()
    {
        using var workspace = CreateMissingUsingWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var (_, error) = await new DiagnosticFixService().ListFixesAsync(
            session, ProjectIdOf(workspace), "CS9999", null, null, null, null, null);
        Assert.IsType<DiagnosticNotFoundError>(error);
    }

    [Fact]
    public async Task list_fixes_duplicate_id_is_ambiguous()
    {
        using var workspace = CreateTwoFileMissingUsingWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var (_, error) = await new DiagnosticFixService().ListFixesAsync(
            session, ProjectIdOf(workspace), "CS0246", null, null, null, null, null);
        Assert.IsType<DiagnosticAmbiguousError>(error);
    }

    [Fact]
    public async Task list_fixes_unsupported_language_is_fix_language_not_supported()
    {
        using var workspace = CreateMissingUsingWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var fake = new FakeAdapter(supportsFix: false);
        var service = new DiagnosticFixService(languages: new LanguageAdapters([fake]));
        var (_, error) = await service.ListFixesAsync(
            session, ProjectIdOf(workspace), "CS0246", null, null, null, null, null);
        Assert.IsType<FixLanguageNotSupportedError>(error);
    }

    [Fact]
    public async Task list_fixes_unavailable_compilation_is_compilation_unavailable()
    {
        using var workspace = CreateMissingUsingWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution, compilationUnavailable: true);
        var (_, error) = await new DiagnosticFixService().ListFixesAsync(
            session, ProjectIdOf(workspace), "CS0246", null, null, null, null, null);
        Assert.IsType<CompilationUnavailableError>(error);
    }

    [Fact]
    public async Task build_preview_occurrence_returns_handwritten_draft()
    {
        using var workspace = CreateMissingUsingWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var service = new DiagnosticFixService();
        var projectId = ProjectIdOf(workspace);
        var (listed, listError) = await service.ListFixesAsync(
            session, projectId, "CS0246", @"C:\fake\Broken.cs", null, null, null, null);
        Assert.Null(listError);
        var index = listed!.Items[0].FixIndex;

        var (draft, error) = await service.BuildPreviewAsync(
            session, projectId, "CS0246", @"C:\fake\Broken.cs", null, null, null, null, index);
        Assert.Null(error);
        Assert.NotNull(draft);
        Assert.NotEmpty(draft!.Documents);
        Assert.Contains(draft.Documents, s => s.NewText != s.OldText);
    }

    [Fact]
    public async Task build_preview_out_of_range_index_is_fix_index_out_of_range()
    {
        using var workspace = CreateMissingUsingWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var projectId = ProjectIdOf(workspace);
        var (_, error) = await new DiagnosticFixService().BuildPreviewAsync(
            session, projectId, "CS0246", @"C:\fake\Broken.cs", null, null, null, null, fixIndex: 99);
        Assert.IsType<FixIndexOutOfRangeError>(error);
    }

    [Fact]
    public async Task build_preview_project_fix_all_time_budget_is_exceeded()
    {
        using var workspace = CreateTwoFileMissingUsingWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var budgets = new SoftBudgetOptions { FixAllProject = TimeSpan.Zero };
        var service = new DiagnosticFixService(budgets: budgets);
        var projectId = ProjectIdOf(workspace);
        var (listed, listError) = await service.ListFixesAsync(
            session, projectId, "CS0246", @"C:\fake\One.cs", null, null, null, null);
        Assert.Null(listError);
        var withKey = listed!.Items.First(i => !string.IsNullOrWhiteSpace(i.EquivalenceKey));

        var (_, error) = await service.BuildPreviewAsync(
            session, projectId, "CS0246", @"C:\fake\One.cs", null, null, null, null,
            withKey.FixIndex, DiagnosticFixScopes.Project);
        Assert.IsType<FixAllBudgetExceededError>(error);
    }

    [Fact]
    public async Task build_preview_unknown_project_is_not_found()
    {
        using var workspace = CreateMissingUsingWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var (_, error) = await new DiagnosticFixService().BuildPreviewAsync(
            session, "missing", "CS0246", null, null, null, null, null, 0);
        Assert.IsType<ProjectNotFoundError>(error);
    }

    [Fact]
    public async Task build_preview_unknown_diagnostic_is_not_found()
    {
        using var workspace = CreateMissingUsingWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var (_, error) = await new DiagnosticFixService().BuildPreviewAsync(
            session, ProjectIdOf(workspace), "CS9999", null, null, null, null, null, 0);
        Assert.IsType<DiagnosticNotFoundError>(error);
    }

    [Fact]
    public async Task build_preview_unavailable_compilation_is_compilation_unavailable()
    {
        using var workspace = CreateMissingUsingWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution, compilationUnavailable: true);
        var (_, error) = await new DiagnosticFixService().BuildPreviewAsync(
            session, ProjectIdOf(workspace), "CS0246", @"C:\fake\Broken.cs", null, null, null, null, 0);
        Assert.IsType<CompilationUnavailableError>(error);
    }
    [Fact]
    public async Task build_preview_document_scope_returns_handwritten_draft()
    {
        using var workspace = CreateMissingUsingWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var service = new DiagnosticFixService();
        var projectId = ProjectIdOf(workspace);
        var (listed, listError) = await service.ListFixesAsync(
            session, projectId, "CS0246", @"C:\fake\Broken.cs", null, null, null, null);
        Assert.Null(listError);
        var withKey = listed!.Items.First(i => !string.IsNullOrWhiteSpace(i.EquivalenceKey));

        var (draft, error) = await service.BuildPreviewAsync(
            session, projectId, "CS0246", @"C:\fake\Broken.cs", null, null, null, null,
            withKey.FixIndex, DiagnosticFixScopes.Document);
        Assert.Null(error);
        Assert.NotNull(draft);
        Assert.Equal(DiagnosticFixScopes.Document, draft!.Scope);
        Assert.NotEmpty(draft.Documents);
    }

    [Fact]
    public async Task build_preview_unsupported_language_is_fix_language_not_supported()
    {
        using var workspace = CreateMissingUsingWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var service = new DiagnosticFixService(languages: new LanguageAdapters([new FakeAdapter(supportsFix: false)]));
        var (_, error) = await service.BuildPreviewAsync(
            session, ProjectIdOf(workspace), "CS0246", @"C:\fake\Broken.cs", null, null, null, null, 0);
        Assert.IsType<FixLanguageNotSupportedError>(error);
    }
    private static string ProjectIdOf(AdhocWorkspace workspace) =>
        workspace.CurrentSolution.Projects.Single().Id.Id.ToString("D");

    private static AdhocWorkspace CreateMissingUsingWorkspace() =>
        CreateWorkspace(
            @"C:\fake\FixApp.csproj",
            ("Broken.cs", @"C:\fake\Broken.cs", MissingUsing("Broken")));

    private static AdhocWorkspace CreateTwoFileMissingUsingWorkspace() =>
        CreateWorkspace(
            @"C:\fake\FixAllApp.csproj",
            ("One.cs", @"C:\fake\One.cs", MissingUsing("One")),
            ("Two.cs", @"C:\fake\Two.cs", MissingUsing("Two")));

    private static string MissingUsing(string typeName) => $$"""
        namespace Lib;
        public class {{typeName}}
        {
            public int Count()
            {
                var items = new List<int>();
                return items.Count;
            }
        }
        """;

    private static AdhocWorkspace CreateWorkspace(string projectFilePath, params (string Name, string Path, string Text)[] docs)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "Lib",
            "Lib",
            LanguageNames.CSharp,
            filePath: projectFilePath));
        foreach (var (name, path, text) in docs)
        {
            solution = solution.AddDocument(DocumentId.CreateNewId(projectId), name, SourceText.From(text), filePath: path);
        }

        solution = solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        foreach (var assembly in new[] { typeof(object).Assembly, typeof(List<>).Assembly })
        {
            solution = solution.AddMetadataReference(projectId, MetadataReference.CreateFromFile(assembly.Location));
        }

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace.");
        }

        return workspace;
    }

    private sealed class FakeSession : IWorkspaceSession
    {
        private readonly bool _compilationUnavailable;

        public FakeSession(Solution solution, bool compilationUnavailable = false)
        {
            Solution = solution;
            Epoch = 1;
            FSharpSnapshot = new FSharpWorkspaceSnapshot(1, []);
            _compilationUnavailable = compilationUnavailable;
        }

        public long Epoch { get; }

        public Solution Solution { get; }

        public FSharpWorkspaceSnapshot FSharpSnapshot { get; }

        public async Task<Compilation> GetCompilationAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default)
        {
            if (_compilationUnavailable)
            {
                throw new InvalidOperationException("Compilation was null.");
            }

            var project = Solution.GetProject(projectId)
                ?? throw new InvalidOperationException($"Project '{projectId.Id}' is not in the session solution.");
            return await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Compilation was null for project '{project.Name}'.");
        }

        public Task<Compilation> GetCompilationWithoutGeneratedTreesAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DriverRunSnapshot> GetGeneratorRunResultAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class FakeAdapter : ILanguageAdapter
    {
        private readonly bool _supportsFix;

        public FakeAdapter(bool supportsFix) => _supportsFix = supportsFix;

        public bool OwnsLanguage(string languageToken) =>
            string.Equals(languageToken, LanguageAdapters.CSharpLanguage, StringComparison.OrdinalIgnoreCase);

        public bool OwnsProject(Project project) => project.Language == LanguageNames.CSharp;

        public bool SupportsCodeRefactoring => false;

        public bool SupportsDiagnosticFix => _supportsFix;

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


        public Task<(SymbolAttributionSuccess? Success, SymbolQueryError? Error)> GetAttributionAsync(
            IWorkspaceSession session,
            string handle,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(PagedResult<DiagnosticItem>? Success, SymbolQueryError? Error)> GetProjectDiagnosticsAsync(
            IWorkspaceSession session, string projectId, int? limit = null, string? cursor = null, TimeSpan? softBudget = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(RenamePreviewDraft? Draft, SymbolQueryError? Error)> BuildRenamePreviewAsync(
            IWorkspaceSession session, string handle, string newName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
