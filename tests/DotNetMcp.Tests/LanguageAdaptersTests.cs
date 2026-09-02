using DotNetMcp.Core;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Tests;

public class LanguageAdaptersTests
{
    [Fact]
    public void try_get_and_for_project_select_one_owning_fake()
    {
        using var workspace = CreateWorkspace(out var csharp, out var fsharp);
        var csharpFake = new FakeAdapter(LanguageAdapters.CSharpLanguage, LanguageNames.CSharp);
        var fsharpFake = new FakeAdapter(LanguageAdapters.FSharpLanguage, LanguageNames.FSharp);
        var adapters = new LanguageAdapters([csharpFake, fsharpFake]);

        Assert.True(adapters.TryGet(LanguageAdapters.CSharpLanguage, out var byCsharp));
        Assert.Same(csharpFake, byCsharp);
        Assert.True(adapters.TryGet(LanguageAdapters.FSharpLanguage, out var byFsharp));
        Assert.Same(fsharpFake, byFsharp);
        Assert.False(adapters.TryGet("python", out _));

        Assert.Same(csharpFake, adapters.ForProject(csharp));
        Assert.Same(fsharpFake, adapters.ForProject(fsharp));
        Assert.Null(adapters.ForProject(workspace.AddProject("VbLib", LanguageNames.VisualBasic)));
    }

    [Fact]
    public void try_get_for_handle_returns_owning_adapter()
    {
        var csharpFake = new FakeAdapter(LanguageAdapters.CSharpLanguage, LanguageNames.CSharp);
        var adapters = new LanguageAdapters([csharpFake]);
        var handle = SymbolHandle.Create(LanguageAdapters.CSharpLanguage, "proj", "Ns.Type").Format();

        Assert.True(adapters.TryGetForHandle(handle, out var adapter, out var error));
        Assert.Same(csharpFake, adapter);
        Assert.Null(error);
    }

    [Fact]
    public void try_get_for_handle_unparseable_is_invalid_symbol_handle()
    {
        var adapters = new LanguageAdapters([new FakeAdapter(LanguageAdapters.CSharpLanguage, LanguageNames.CSharp)]);

        Assert.False(adapters.TryGetForHandle("", out var adapter, out var error));
        Assert.Null(adapter);
        Assert.IsType<InvalidSymbolHandleError>(error);
    }

    [Fact]
    public void try_get_for_handle_unknown_language_is_invalid_symbol_handle()
    {
        var adapters = new LanguageAdapters([new FakeAdapter(LanguageAdapters.CSharpLanguage, LanguageNames.CSharp)]);
        var handle = SymbolHandle.Create(LanguageAdapters.FSharpLanguage, "proj", "FsLib.Widget").Format();

        Assert.False(adapters.TryGetForHandle(handle, out var adapter, out var error));
        Assert.Null(adapter);
        Assert.IsType<InvalidSymbolHandleError>(error);
    }

    [Fact]
    public async Task resolve_by_name_blank_is_symbol_not_found_without_calling_fakes()
    {
        using var workspace = CreateWorkspace(out _, out _);
        var csharpFake = new FakeAdapter(LanguageAdapters.CSharpLanguage, LanguageNames.CSharp);
        var adapters = new LanguageAdapters([csharpFake]);
        using var session = new FakeSession(workspace.CurrentSolution);

        var (_, error) = await adapters.ResolveByNameAsync(session, "  ");

        Assert.IsType<SymbolNotFoundError>(error);
        Assert.Equal(0, csharpFake.ResolveCalls);
    }

    [Fact]
    public async Task resolve_by_name_unknown_project_is_symbol_not_found_without_calling_fakes()
    {
        using var workspace = CreateWorkspace(out _, out _);
        var csharpFake = new FakeAdapter(LanguageAdapters.CSharpLanguage, LanguageNames.CSharp);
        var adapters = new LanguageAdapters([csharpFake]);
        using var session = new FakeSession(workspace.CurrentSolution);

        var (_, error) = await adapters.ResolveByNameAsync(session, "Widget", projectId: "missing");

        Assert.IsType<SymbolNotFoundError>(error);
        Assert.Equal(0, csharpFake.ResolveCalls);
    }

    [Fact]
    public async Task resolve_by_name_with_project_id_calls_only_owning_fake()
    {
        using var workspace = CreateWorkspace(out var csharp, out _);
        var csharpFake = new FakeAdapter(LanguageAdapters.CSharpLanguage, LanguageNames.CSharp);
        var fsharpFake = new FakeAdapter(LanguageAdapters.FSharpLanguage, LanguageNames.FSharp);
        var adapters = new LanguageAdapters([csharpFake, fsharpFake]);
        using var session = new FakeSession(workspace.CurrentSolution);

        var (success, error) = await adapters.ResolveByNameAsync(
            session,
            "Widget",
            projectId: csharp.Id.Id.ToString("D"));

        Assert.Null(error);
        Assert.NotNull(success);
        Assert.Equal(1, csharpFake.ResolveCalls);
        Assert.Equal(0, fsharpFake.ResolveCalls);
    }

    [Fact]
    public async Task get_attribution_dispatches_to_handle_owner()
    {
        var csharpFake = new FakeAdapter(LanguageAdapters.CSharpLanguage, LanguageNames.CSharp);
        var fsharpFake = new FakeAdapter(LanguageAdapters.FSharpLanguage, LanguageNames.FSharp);
        var adapters = new LanguageAdapters([csharpFake, fsharpFake]);
        using var workspace = CreateWorkspace(out _, out _);
        using var session = new FakeSession(workspace.CurrentSolution);
        var handle = SymbolHandle.Create(LanguageAdapters.CSharpLanguage, "proj", "Ns.Type").Format();

        var (success, error) = await adapters.GetAttributionAsync(session, handle);

        Assert.Null(error);
        Assert.NotNull(success);
        Assert.Equal(1, csharpFake.AttributionCalls);
        Assert.Equal(0, fsharpFake.AttributionCalls);
        Assert.Equal(SymbolOrigin.Handwritten, success!.Attribution.OriginKind);
    }

    [Fact]
    public async Task build_rename_preview_rejects_illegal_name_before_selecting_adapter()
    {
        var csharpFake = new FakeAdapter(LanguageAdapters.CSharpLanguage, LanguageNames.CSharp);
        var adapters = new LanguageAdapters([csharpFake]);
        using var workspace = CreateWorkspace(out _, out _);
        using var session = new FakeSession(workspace.CurrentSolution);
        var handle = SymbolHandle.Create(LanguageAdapters.CSharpLanguage, "proj", "Ns.Type").Format();

        var (_, dotted) = await adapters.BuildRenamePreviewAsync(session, handle, "A.B");
        var (_, blank) = await adapters.BuildRenamePreviewAsync(session, handle, " ");

        Assert.IsType<InvalidRenameNameError>(dotted);
        Assert.IsType<InvalidRenameNameError>(blank);
        Assert.Equal(0, csharpFake.RenameCalls);
    }

    [Fact]
    public async Task build_rename_preview_bad_handle_is_invalid_symbol_handle()
    {
        var csharpFake = new FakeAdapter(LanguageAdapters.CSharpLanguage, LanguageNames.CSharp);
        var adapters = new LanguageAdapters([csharpFake]);
        using var workspace = CreateWorkspace(out _, out _);
        using var session = new FakeSession(workspace.CurrentSolution);

        var (_, error) = await adapters.BuildRenamePreviewAsync(session, "not-a-handle", "NewName");

        Assert.IsType<InvalidSymbolHandleError>(error);
        Assert.Equal(0, csharpFake.RenameCalls);
    }

    [Fact]
    public async Task build_rename_preview_forwards_new_name_to_owning_fake()
    {
        var csharpFake = new FakeAdapter(LanguageAdapters.CSharpLanguage, LanguageNames.CSharp);
        var adapters = new LanguageAdapters([csharpFake]);
        using var workspace = CreateWorkspace(out _, out _);
        using var session = new FakeSession(workspace.CurrentSolution);
        var handle = SymbolHandle.Create(LanguageAdapters.CSharpLanguage, "proj", "Ns.Type").Format();

        var (draft, error) = await adapters.BuildRenamePreviewAsync(session, handle, "NewName");

        Assert.Null(error);
        Assert.NotNull(draft);
        Assert.Equal("NewName", draft!.NewName);
        Assert.Equal(1, csharpFake.RenameCalls);
        Assert.Equal("NewName", csharpFake.LastRenameName);
    }

    private static AdhocWorkspace CreateWorkspace(out Project csharp, out Project fsharp)
    {
        var workspace = new AdhocWorkspace();
        csharp = workspace.AddProject("CsLib", LanguageNames.CSharp);
        fsharp = workspace.AddProject("FsLib", LanguageNames.FSharp);
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

        public async Task<Compilation> GetCompilationAsync(ProjectId projectId, CancellationToken cancellationToken = default)
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
        private readonly string _language;
        private readonly string _projectLanguage;

        public FakeAdapter(string language, string projectLanguage)
        {
            _language = language;
            _projectLanguage = projectLanguage;
        }

        public int ResolveCalls { get; private set; }

        public int RenameCalls { get; private set; }

        public string? LastRenameName { get; private set; }

        public bool OwnsLanguage(string languageToken) =>
            string.Equals(languageToken, _language, StringComparison.OrdinalIgnoreCase);

        public bool OwnsProject(Project project) => project.Language == _projectLanguage;

        public bool SupportsCodeRefactoring => false;

        public bool SupportsDiagnosticFix => false;

        public Task<(SymbolResolveSuccess? Success, SymbolQueryError? Error)> ResolveByNameAsync(
            IWorkspaceSession session,
            string name,
            string? projectId = null,
            CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            var handle = SymbolHandle.Create(_language, projectId ?? "proj", name).Format();
            var summary = new SymbolSummary("NamedType", name, null, "Public", projectId ?? "proj", _language);
            return Task.FromResult<(SymbolResolveSuccess?, SymbolQueryError?)>(
                (new SymbolResolveSuccess(handle, summary), null));
        }

        public Task<(RenamePreviewDraft? Draft, SymbolQueryError? Error)> BuildRenamePreviewAsync(
            IWorkspaceSession session,
            string handle,
            string newName,
            CancellationToken cancellationToken = default)
        {
            RenameCalls++;
            LastRenameName = newName;
            return Task.FromResult<(RenamePreviewDraft?, SymbolQueryError?)>(
                (new RenamePreviewDraft(handle, newName, [], []), null));
        }

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


        public int AttributionCalls { get; private set; }

        public Task<(SymbolAttributionSuccess? Success, SymbolQueryError? Error)> GetAttributionAsync(
            IWorkspaceSession session,
            string handle,
            CancellationToken cancellationToken = default)
        {
            AttributionCalls++;
            var attribution = new SymbolAttribution(
                DeclarationAvailability.InSource,
                SymbolOrigin.Handwritten,
                Generator: null);
            return Task.FromResult<(SymbolAttributionSuccess?, SymbolQueryError?)>(
                (new SymbolAttributionSuccess(attribution, new Dictionary<string, SymbolAttribution>()), null));
        }

        public Task<(PagedResult<DiagnosticItem>? Success, SymbolQueryError? Error)> GetProjectDiagnosticsAsync(
            IWorkspaceSession session,
            string projectId,
            int? limit = null,
            string? cursor = null,
            TimeSpan? softBudget = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
