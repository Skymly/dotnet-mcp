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
    public async Task batch_diagnostics_pages_past_100_and_marks_project_errors()
    {
        using var workspace = CreateTwoProjectWorkspace();
        var projects = workspace.CurrentSolution.Projects.OrderBy(p => p.Name).ToArray();
        var fake = new FakeAdapter
        {
            PagesByProject =
            {
                [projects[0].Id.Id.ToString("D")] = Enumerable.Range(0, 130)
                    .Select(i => new DiagnosticItem("CS0000", "Warning", $"w{i}", @"C:.cs", i, 0, i, 1, projects[0].Id.Id.ToString("D")))
                    .ToList(),
            },
            ErrorsByProject =
            {
                [projects[1].Id.Id.ToString("D")] = new CompilationUnavailableError(
                    "compile missing",
                    "retry"),
            }
        };
        var service = new DiagnosticQueryService(languages: new LanguageAdapters([fake]));
        using var session = new FakeSession(workspace.CurrentSolution);

        var all = new List<DiagnosticItem>();
        string? cursor = null;
        PagedResult<DiagnosticItem>? last = null;
        for (var i = 0; i < 10; i++)
        {
            var (success, error) = await service.GetProjectDiagnosticsAsync(
                session, projectId: "  ", cursor: cursor);
            Assert.Null(error);
            Assert.NotNull(success);
            last = success;
            all.AddRange(success!.Items);
            if (!success.Truncated)
            {
                break;
            }

            cursor = success.NextCursor;
            Assert.False(string.IsNullOrWhiteSpace(cursor));
        }

        Assert.NotNull(last);
        Assert.Contains(all, i => i.Id == "CS0000");
        Assert.Contains(all, i => i.Id == SymbolQueryErrorCodes.CompilationUnavailable);
        Assert.True(all.Count >= 131, $"count={all.Count}");
        Assert.Contains("failed", last!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fake.Cursors, c => c is not null);
    }

    [Fact]
    public async Task batch_diagnostics_budget_hit_with_items_that_fit_one_page_is_truncated_and_continues()
    {
        using var workspace = CreateTwoProjectWorkspace();
        var projects = workspace.CurrentSolution.Projects.OrderBy(p => p.Name).ToArray();
        var projectA = projects[0].Id.Id.ToString("D");
        var projectB = projects[1].Id.Id.ToString("D");
        var fake = new FakeAdapter
        {
            TreatShortBudgetAsHit = true,
            PagesByProject =
            {
                [projectA] =
                [
                    new DiagnosticItem("CS0001", "Error", "a1", @"C:\a.cs", 1, 0, 1, 1, projectA),
                    new DiagnosticItem("CS0002", "Error", "a2", @"C:\a.cs", 2, 0, 2, 1, projectA),
                ],
                [projectB] =
                [
                    new DiagnosticItem("CS0003", "Error", "b1", @"C:\b.cs", 1, 0, 1, 1, projectB),
                ],
            }
        };
        var service = new DiagnosticQueryService(languages: new LanguageAdapters([fake]));
        using var session = new FakeSession(workspace.CurrentSolution);

        var (page, error) = await service.GetProjectDiagnosticsAsync(
            session,
            projectId: string.Empty,
            limit: 50,
            softBudget: TimeSpan.FromSeconds(1));

        Assert.Null(error);
        Assert.NotNull(page);
        Assert.Equal(2, page!.Items.Count);
        Assert.True(page.Truncated);
        Assert.False(string.IsNullOrWhiteSpace(page.NextCursor));
        Assert.DoesNotContain("complete", page.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no error or warning", page.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Soft budget", page.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(page.Items, i => i.Id == "CS0003");

        var (continued, continueError) = await service.GetProjectDiagnosticsAsync(
            session,
            projectId: string.Empty,
            limit: 50,
            cursor: page.NextCursor,
            softBudget: TimeSpan.FromSeconds(30));

        Assert.Null(continueError);
        Assert.NotNull(continued);
        Assert.Contains(continued!.Items, i => i.Id == "CS0003");
        Assert.DoesNotContain(continued.Items, i => i.Id == "CS0001");
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

    private static AdhocWorkspace CreateTwoProjectWorkspace()
    {
        var workspace = new AdhocWorkspace();
        workspace.AddProject("LibA", LanguageNames.CSharp);
        workspace.AddProject("LibB", LanguageNames.CSharp);
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

        public Dictionary<string, List<DiagnosticItem>> PagesByProject { get; } = new();

        public Dictionary<string, SymbolQueryError> ErrorsByProject { get; } = new();

        public int DiagnosticCalls { get; private set; }

        public string? LastProjectId { get; private set; }

        public int? LastLimit { get; private set; }

        public string? LastCursor { get; private set; }

        public TimeSpan? LastSoftBudget { get; private set; }

        public bool TreatShortBudgetAsHit { get; set; }

        public List<string?> Cursors { get; } = [];

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
            Cursors.Add(cursor);
            if (ErrorsByProject.TryGetValue(projectId, out var projectError))
            {
                return Task.FromResult<(PagedResult<DiagnosticItem>?, SymbolQueryError?)>((null, projectError));
            }

            if (PagesByProject.TryGetValue(projectId, out var all))
            {
                var offset = 0;
                if (!string.IsNullOrWhiteSpace(cursor) &&
                    MemberPageCursor.TryDecode(cursor, out _, out var decoded, out _, out _, out _))
                {
                    offset = decoded;
                }

                var take = limit is null or <= 0 ? 50 : Math.Min(limit.Value, 100);
                var slice = all.Skip(offset).Take(take).ToList();
                var next = offset + slice.Count;
                var truncated = next < all.Count;
                var page = new PagedResult<DiagnosticItem>(
                    slice,
                    truncated,
                    truncated ? MemberPageCursor.Encode(1, next, "project_diagnostics", projectId) : null,
                    truncated ? "Results truncated; pass nextCursor" : "done");
                if (TreatShortBudgetAsHit &&
                    softBudget is { } budget &&
                    budget > TimeSpan.Zero &&
                    budget < TimeSpan.FromSeconds(10))
                {
                    page = page with
                    {
                        Truncated = true,
                        NextCursor = page.NextCursor ?? MemberPageCursor.Encode(1, next, "project_diagnostics", projectId),
                        Message = $"Soft budget reached after {slice.Count} item(s). Pass nextCursor to project_diagnostics to continue; do not retry from scratch."
                    };
                }

                return Task.FromResult<(PagedResult<DiagnosticItem>?, SymbolQueryError?)>((page, null));
            }

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
            bool entireSolution = false,
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
