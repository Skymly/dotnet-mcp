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
            [new DiagnosticItem("CS0103", "Error", "name missing", @"C:\fake\W.cs", 1, 1, 1, 4, projectId)],
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
        Assert.Same(page, success!.Page);
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
                    .Select(i => new DiagnosticItem("CS0000", "Warning", $"w{i}", @"C:.cs", i + 1, 1, i + 1, 2, projects[0].Id.Id.ToString("D")))
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
            last = success!.Page;
            all.AddRange(success.Page.Items);
            if (!success.Page.Truncated)
            {
                break;
            }

            cursor = success.Page.NextCursor;
            Assert.False(string.IsNullOrWhiteSpace(cursor));
        }

        Assert.NotNull(last);
        Assert.Contains(all, i => i.Id == "CS0000");
        Assert.Contains(all, i => i.Error == SymbolQueryErrorCodes.CompilationUnavailable && i.Severity != "Error");
        Assert.DoesNotContain(all, i => i.Id == SymbolQueryErrorCodes.CompilationUnavailable);
        Assert.True(all.Count >= 131, $"count={all.Count}");
        Assert.Contains("failed", last!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fake.Cursors, c => c is not null);
    }

    [Fact]
    public async Task batch_project_failure_is_not_a_diagnostic_row()
    {
        using var workspace = CreateTwoProjectWorkspace();
        var projects = workspace.CurrentSolution.Projects.OrderBy(p => p.Name).ToArray();
        var okId = projects[0].Id.Id.ToString("D");
        var failedId = projects[1].Id.Id.ToString("D");
        var fake = new FakeAdapter
        {
            PagesByProject =
            {
                [okId] = [new DiagnosticItem("CS0001", "Error", "real", @"C:\a.cs", 1, 1, 1, 2, okId)],
            },
            ErrorsByProject =
            {
                [failedId] = new CompilationUnavailableError("compile missing", "Rebuild the project, then retry project_diagnostics."),
            }
        };
        var service = new DiagnosticQueryService(languages: new LanguageAdapters([fake]));
        using var session = new FakeSession(workspace.CurrentSolution);

        var (result, error) = await service.GetProjectDiagnosticsAsync(session, projectId: string.Empty);
        Assert.Null(error);
        Assert.NotNull(result);
        var page = result!.Page;
        var failure = Assert.Single(page.Items, i => i.ProjectId == failedId);
        Assert.Equal(SymbolQueryErrorCodes.CompilationUnavailable, failure.Error);
        Assert.Equal("Rebuild the project, then retry project_diagnostics.", failure.SuggestedAction);
        Assert.NotEqual("Error", failure.Severity);
        Assert.NotEqual(SymbolQueryErrorCodes.CompilationUnavailable, failure.Id);
        Assert.Contains(page.Items, i => i.Id == "CS0001" && i.Severity == "Error" && i.Error is null);
        Assert.Contains("failed", page.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not a clean project", page.Message, StringComparison.OrdinalIgnoreCase);
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
                    new DiagnosticItem("CS0001", "Error", "a1", @"C:\a.cs", 1, 1, 1, 2, projectA),
                    new DiagnosticItem("CS0002", "Error", "a2", @"C:\a.cs", 2, 1, 2, 2, projectA),
                ],
                [projectB] =
                [
                    new DiagnosticItem("CS0003", "Error", "b1", @"C:\b.cs", 1, 1, 1, 2, projectB),
                ],
            }
        };
        var service = new DiagnosticQueryService(languages: new LanguageAdapters([fake]));
        using var session = new FakeSession(workspace.CurrentSolution);

        var (first, error) = await service.GetProjectDiagnosticsAsync(
            session,
            projectId: string.Empty,
            limit: 50,
            softBudget: TimeSpan.FromSeconds(1));

        Assert.Null(error);
        Assert.NotNull(first);
        var page = first!.Page;
        Assert.Equal(2, page.Items.Count);
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
        Assert.Contains(continued!.Page.Items, i => i.Id == "CS0003");
        Assert.DoesNotContain(continued.Page.Items, i => i.Id == "CS0001");
    }

    [Theory]
    [InlineData("CS0006", true)]
    [InlineData("CS0012", true)]
    [InlineData("CS0234", false)]
    [InlineData("CS0246", false)]
    [InlineData("CS1061", false)]
    [InlineData("DependencyOutputNotBuilt", false)]
    [InlineData("FS0039", false)]
    public void missing_metadata_detection_keys_on_cs0006_cs0012_only(string id, bool expected)
    {
        Assert.Equal(expected, MissingMetadataDiagnostics.IsMissingMetadataDiagnostic(id));
    }

    [Fact]
    public async Task get_project_diagnostics_marks_degraded_when_adapter_reports_cs0012()
    {
        using var workspace = CreateWorkspace();
        var projectId = workspace.CurrentSolution.Projects.Single().Id.Id.ToString("D");
        var fake = new FakeAdapter
        {
            PagesByProject =
            {
                [projectId] =
                [
                    new DiagnosticItem("CS0012", "Error", "type defined in unreferenced assembly", @"C:\a.cs", 1, 1, 1, 2, projectId),
                    new DiagnosticItem("CS0246", "Error", "real typo", @"C:\a.cs", 2, 1, 2, 2, projectId),
                ],
            },
        };
        var service = new DiagnosticQueryService(languages: new LanguageAdapters([fake]));
        using var session = new FakeSession(workspace.CurrentSolution);

        var (result, error) = await service.GetProjectDiagnosticsAsync(session, projectId);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.True(result!.Degraded);
        Assert.Equal(1, result.MissingMetadataDiagnosticCount);
        Assert.Equal(2, result.Page.Items.Count);
    }

    [Fact]
    public async Task get_project_diagnostics_is_not_degraded_for_cs0246_only()
    {
        using var workspace = CreateWorkspace();
        var projectId = workspace.CurrentSolution.Projects.Single().Id.Id.ToString("D");
        var fake = new FakeAdapter
        {
            PagesByProject =
            {
                [projectId] = [new DiagnosticItem("CS0246", "Error", "type not found", @"C:\a.cs", 1, 1, 1, 2, projectId)],
            },
        };
        var service = new DiagnosticQueryService(languages: new LanguageAdapters([fake]));
        using var session = new FakeSession(workspace.CurrentSolution);

        var (result, error) = await service.GetProjectDiagnosticsAsync(session, projectId);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.False(result!.Degraded);
        Assert.Equal(0, result.MissingMetadataDiagnosticCount);
    }

    [Fact]
    public async Task batch_diagnostics_marks_degraded_when_any_project_reports_cs0006()
    {
        using var workspace = CreateTwoProjectWorkspace();
        var projects = workspace.CurrentSolution.Projects.OrderBy(p => p.Name).ToArray();
        var projectA = projects[0].Id.Id.ToString("D");
        var projectB = projects[1].Id.Id.ToString("D");
        var fake = new FakeAdapter
        {
            PagesByProject =
            {
                [projectA] = [new DiagnosticItem("CS0006", "Error", "metadata file missing", @"C:\a.cs", 1, 1, 1, 2, projectA)],
                [projectB] = [new DiagnosticItem("CS0246", "Error", "type not found", @"C:\b.cs", 1, 1, 1, 2, projectB)],
            },
        };
        var service = new DiagnosticQueryService(languages: new LanguageAdapters([fake]));
        using var session = new FakeSession(workspace.CurrentSolution);

        var (result, error) = await service.GetProjectDiagnosticsAsync(session, projectId: string.Empty);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.True(result!.Degraded);
        Assert.Equal(1, result.MissingMetadataDiagnosticCount);
    }

    [Fact]
    public async Task batch_diagnostics_marks_degraded_when_any_project_has_unmaterialized_reference()
    {
        using var workspace = CreateTwoProjectWorkspace();
        var projects = workspace.CurrentSolution.Projects.OrderBy(p => p.Name).ToArray();
        var projectA = projects[0].Id.Id.ToString("D");
        var projectB = projects[1].Id.Id.ToString("D");
        var fake = new FakeAdapter
        {
            PagesByProject =
            {
                [projectA] = [new DiagnosticItem("CS0246", "Error", "cascade from missing ref", @"C:\a.cs", 1, 1, 1, 2, projectA)],
                [projectB] = [new DiagnosticItem("CS0168", "Warning", "unused", @"C:\b.cs", 1, 1, 1, 2, projectB)],
            },
            UnmaterializedByProject =
            {
                [projectA] = [@"C:\fake\MissingDep\MissingDep.csproj"],
            },
        };
        var service = new DiagnosticQueryService(languages: new LanguageAdapters([fake]));
        using var session = new FakeSession(workspace.CurrentSolution);

        var (result, error) = await service.GetProjectDiagnosticsAsync(session, projectId: string.Empty);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.True(result!.Degraded);
        Assert.Equal(0, result.MissingMetadataDiagnosticCount);
        var missing = Assert.Single(result.UnmaterializedReferences);
        Assert.Contains("MissingDep", missing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task batch_diagnostics_marks_degraded_and_dedups_unresolved_analyzer_paths_across_inner_pages()
    {
        using var workspace = CreateWorkspace();
        var projectId = workspace.CurrentSolution.Projects.Single().Id.Id.ToString("D");
        var analyzerPath = @"C:\fake\gen\Missing.Generators.dll";
        var fake = new FakeAdapter
        {
            PagesByProject =
            {
                [projectId] = Enumerable.Range(0, 130)
                    .Select(i => new DiagnosticItem("CS0246", "Error", $"cascade {i}", @"C:\a.cs", i + 1, 1, i + 1, 2, projectId))
                    .ToList(),
            },
            UnresolvedAnalyzersByProject =
            {
                [projectId] = [analyzerPath],
            },
        };
        var service = new DiagnosticQueryService(languages: new LanguageAdapters([fake]));
        using var session = new FakeSession(workspace.CurrentSolution);

        var (result, error) = await service.GetProjectDiagnosticsAsync(session, projectId: string.Empty);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.True(result!.Degraded);
        Assert.Equal(0, result.MissingMetadataDiagnosticCount);
        // 130 items page through two inner adapter calls; the declared path
        // reports on every inner page and must still aggregate once.
        var analyzer = Assert.Single(result.UnresolvedAnalyzers);
        Assert.Equal(analyzerPath, analyzer);
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

        public Dictionary<string, List<string>> UnmaterializedByProject { get; } = new();

        public Dictionary<string, List<string>> UnresolvedAnalyzersByProject { get; } = new();

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

        public bool SupportsSourceGenerators => false;

        public bool SupportsDynamicInvocations => false;


        public Task<(SymbolAttributionSuccess? Success, SymbolQueryError? Error)> GetAttributionAsync(
            IWorkspaceSession session,
            string handle,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(SymbolSourceSuccess? Success, SymbolQueryError? Error)> GetSourceAsync(
            IWorkspaceSession session,
            string handle,
            int maxChars,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(ProjectDiagnosticsResult? Success, SymbolQueryError? Error)> GetProjectDiagnosticsAsync(
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
                return Task.FromResult<(ProjectDiagnosticsResult?, SymbolQueryError?)>((null, projectError));
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

                return Task.FromResult<(ProjectDiagnosticsResult?, SymbolQueryError?)>(
                    (new ProjectDiagnosticsResult(
                        page,
                        MissingMetadataDiagnostics.CountIn(all),
                        UnmaterializedByProject.TryGetValue(projectId, out var refs) ? refs : null,
                        UnresolvedAnalyzersByProject.TryGetValue(projectId, out var analyzers) ? analyzers : null), null));
            }

            return Task.FromResult<(ProjectDiagnosticsResult?, SymbolQueryError?)>(
                (DiagnosticsPage is null
                    ? null
                    : new ProjectDiagnosticsResult(
                        DiagnosticsPage,
                        MissingMetadataDiagnostics.CountIn(DiagnosticsPage.Items)), null));
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

        public Task<(SymbolResolveAtSuccess? Success, SymbolQueryError? Error)> ResolveAtPositionAsync(
            IWorkspaceSession session,
            string path,
            int line,
            int column,
            string? projectId = null,
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
