using DotNetMcp.Core;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Tests;

public class DiagnosticQueryServiceTests
{
    [Fact]
    public async Task get_project_diagnostics_forwards_page_from_owning_adapter()
    {
        using var workspace = CreateWorkspace();
        var projectId = workspace.CurrentSolution.Projects.Single().Id.Id.ToString("D");
        var fake = new FakeAdapter();
        var page = new PagedResult<DiagnosticItem>(
            [new DiagnosticItem("CS0103", "Error", "name missing", @"C:\fake\W.cs", 1, 0, 1, 4, projectId)],
            Truncated: false,
            NextCursor: null,
            Message: "done");
        fake.DiagnosticsPage = page;
        var service = new DiagnosticQueryService(languages: new LanguageAdapters([fake]));
        using var session = new FakeSession(workspace.CurrentSolution);

        var (success, error) = await service.GetProjectDiagnosticsAsync(
            session,
            projectId,
            limit: 7,
            cursor: "c",
            softBudget: TimeSpan.FromSeconds(1));

        Assert.Null(error);
        Assert.Same(page, success);
        Assert.Equal(1, fake.DiagnosticCalls);
        Assert.Equal(projectId, fake.LastProjectId);
        Assert.Equal(7, fake.LastLimit);
        Assert.Equal("c", fake.LastCursor);
        Assert.Equal(TimeSpan.FromSeconds(1), fake.LastSoftBudget);
    }

    [Fact]
    public async Task get_project_diagnostics_unknown_project_is_not_found()
    {
        using var workspace = CreateWorkspace();
        var fake = new FakeAdapter();
        var service = new DiagnosticQueryService(languages: new LanguageAdapters([fake]));
        using var session = new FakeSession(workspace.CurrentSolution);

        var (_, error) = await service.GetProjectDiagnosticsAsync(session, "missing-project");

        Assert.IsType<ProjectNotFoundError>(error);
        Assert.Equal(0, fake.DiagnosticCalls);
    }

    private static AdhocWorkspace CreateWorkspace()
    {
        var workspace = new AdhocWorkspace();
        workspace.AddProject("Lib", LanguageNames.CSharp);
        return workspace;
    }

    private sealed class FakeSession : IWorkspaceSession
    {
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
        public PagedResult<DiagnosticItem>? DiagnosticsPage { get; set; }

        public int DiagnosticCalls { get; private set; }

        public string? LastProjectId { get; private set; }

        public int? LastLimit { get; private set; }

        public string? LastCursor { get; private set; }

        public TimeSpan? LastSoftBudget { get; private set; }

        public bool OwnsLanguage(string languageToken) =>
            string.Equals(languageToken, LanguageAdapters.CSharpLanguage, StringComparison.OrdinalIgnoreCase);

        public bool OwnsProject(Project project) => project.Language == LanguageNames.CSharp;

        public bool SupportsCodeRefactoring => false;

        public bool SupportsDiagnosticFix => false;


        public Task<(SymbolAttributionSuccess? Success, SymbolQueryError? Error)> GetAttributionAsync(
            IWorkspaceSession session,
            string handle,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(PagedResult<DiagnosticItem>? Success, SymbolQueryError? Error)> GetProjectDiagnosticsAsync(
            IWorkspaceSession session,
            string projectId,
            int? limit = null,
            string? cursor = null,
            TimeSpan? softBudget = null,
            CancellationToken cancellationToken = default)
        {
            DiagnosticCalls++;
            LastProjectId = projectId;
            LastLimit = limit;
            LastCursor = cursor;
            LastSoftBudget = softBudget;
            return Task.FromResult<(PagedResult<DiagnosticItem>?, SymbolQueryError?)>((DiagnosticsPage, null));
        }

        public Task<(SymbolResolveSuccess? Success, SymbolQueryError? Error)> ResolveByNameAsync(
            IWorkspaceSession session,
            string name,
            string? projectId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(SymbolResolveSuccess? Success, SymbolQueryError? Error)> GetSummaryAsync(
            IWorkspaceSession session,
            string handle,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(SymbolDefinitionSuccess? Success, SymbolQueryError? Error)> GetDefinitionAsync(
            IWorkspaceSession session,
            string handle,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(PagedResult<MemberListItem>? Success, SymbolQueryError? Error)> GetMembersAsync(
            IWorkspaceSession session,
            string handle,
            int? limit = null,
            string? cursor = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(PagedResult<ReferenceLocationItem>? Success, SymbolQueryError? Error)> FindReferencesAsync(
            IWorkspaceSession session,
            string handle,
            bool entireSolution = false,
            int? limit = null,
            string? cursor = null,
            TimeSpan? softBudget = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(PagedResult<ImplementationItem>? Success, SymbolQueryError? Error)> FindImplementationsAsync(
            IWorkspaceSession session,
            string handle,
            int? limit = null,
            string? cursor = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(PagedResult<HierarchyItem>? Success, SymbolQueryError? Error)> GetTypeHierarchyAsync(
            IWorkspaceSession session,
            string handle,
            int? limit = null,
            string? cursor = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(PagedResult<CallerLocationItem>? Success, SymbolQueryError? Error)> FindCallersAsync(
            IWorkspaceSession session,
            string handle,
            int? limit = null,
            string? cursor = null,
            TimeSpan? softBudget = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(RenamePreviewDraft? Draft, SymbolQueryError? Error)> BuildRenamePreviewAsync(
            IWorkspaceSession session,
            string handle,
            string newName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
