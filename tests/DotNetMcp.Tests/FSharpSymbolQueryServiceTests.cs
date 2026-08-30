using DotNetMcp.Core;
using DotNetMcp.FSharp;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Tests;

public class FSharpSymbolQueryServiceTests
{
    private const string FsProjectId = "11111111-1111-1111-1111-111111111111";
    private const string OtherProjectId = "22222222-2222-2222-2222-222222222222";
    private const string WidgetPath = @"C:\fake-fs-unit\FsLib\Widget.fs";
    private const string UsesPath = @"C:\fake-fs-unit\FsLib\Uses.fs";
    private const string BrokenPath = @"C:\fake-fs-unit\Broken\Broken.fs";

    private const string WidgetSource = """
        module FsLib.Widget

        type Gadget() =
            member _.Ping() = 1
            member _.Pong() = 2

        let ping () = 1
        """;

    private const string UsesSource = """
        module FsLib.Uses

        let go () = Widget.ping()
        """;

    private const string BrokenSource = """
        module Broken

        let alpha: int = "not-an-int"
        """;

    private static FSharpSymbolQueryService Adapter() => new();

    [Theory]
    [InlineData("fsharp", true)]
    [InlineData("csharp", false)]
    [InlineData("vb", false)]
    [InlineData("python", false)]
    public void owns_language(string token, bool expected) =>
        Assert.Equal(expected, Adapter().OwnsLanguage(token));

    [Fact]
    public void owns_project_fsharp_language_or_fsproj()
    {
        using var workspace = new AdhocWorkspace();
        var fsharp = workspace.AddProject("FsLib", LanguageNames.FSharp);
        var csharp = workspace.AddProject("CsLib", LanguageNames.CSharp);
        var fsproj = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "NamedCs",
            "NamedCs",
            LanguageNames.CSharp,
            filePath: @"C:\fake\Lib.fsproj"));

        var adapter = Adapter();
        Assert.True(adapter.OwnsProject(fsharp));
        Assert.False(adapter.OwnsProject(csharp));
        Assert.True(adapter.OwnsProject(fsproj));
    }

    [Fact]
    public void capability_flags_are_false()
    {
        var adapter = Adapter();
        Assert.False(adapter.SupportsCodeRefactoring);
        Assert.False(adapter.SupportsDiagnosticFix);
    }

    [Fact]
    public async Task resolve_by_name_unique_fsharp_type_succeeds()
    {
        using var session = Session(WidgetSnapshot());
        var (success, error) = await Adapter().ResolveByNameAsync(session, "Gadget");

        Assert.Null(error);
        Assert.NotNull(success);
        Assert.True(SymbolHandle.TryParse(success!.Handle, out var parsed, out _));
        Assert.Equal("fsharp", parsed!.Language);
        Assert.Equal("Gadget", success.Summary.DisplayName);
    }

    [Fact]
    public async Task resolve_by_name_blank_is_not_found()
    {
        using var session = Session(WidgetSnapshot());
        var (_, error) = await Adapter().ResolveByNameAsync(session, "  ");
        Assert.IsType<SymbolNotFoundError>(error);
    }

    [Fact]
    public async Task resolve_by_name_missing_is_not_found()
    {
        using var session = Session(WidgetSnapshot());
        var (_, error) = await Adapter().ResolveByNameAsync(session, "DoesNotExist");
        Assert.IsType<SymbolNotFoundError>(error);
    }

    [Fact]
    public async Task resolve_by_name_unknown_project_is_not_found()
    {
        using var session = Session(WidgetSnapshot());
        var (_, error) = await Adapter().ResolveByNameAsync(session, "Widget", projectId: "missing");
        Assert.IsType<SymbolNotFoundError>(error);
    }

    [Fact]
    public async Task resolve_by_name_two_projects_is_ambiguous()
    {
        var snapshot = new FSharpWorkspaceSnapshot(1, [
            SnapshotProject(FsProjectId, "FsLib", WidgetPath, WidgetSource),
            SnapshotProject(OtherProjectId, "OtherFs", @"C:\fake-fs-unit\Other\Widget.fs", WidgetSource),
        ]);
        using var session = Session(snapshot);
        var (_, error) = await Adapter().ResolveByNameAsync(session, "Gadget");
        Assert.IsType<SymbolAmbiguousError>(error);
    }

    [Fact]
    public async Task get_members_returns_first_page_not_truncated()
    {
        using var session = Session(WidgetSnapshot());
        var adapter = Adapter();
        var (resolved, resolveError) = await adapter.ResolveByNameAsync(session, "Gadget");
        Assert.Null(resolveError);

        var (page, error) = await adapter.GetMembersAsync(session, resolved!.Handle);
        Assert.Null(error);
        Assert.NotNull(page);
        Assert.Contains(page!.Items, i => i.Summary.DisplayName == "Ping");
        Assert.Contains(page.Items, i => i.Summary.DisplayName == "Pong");
        Assert.False(page.Truncated);
    }

    [Fact]
    public async Task get_members_unparseable_handle_is_invalid()
    {
        using var session = Session(WidgetSnapshot());
        var (_, error) = await Adapter().GetMembersAsync(session, "");
        Assert.IsType<InvalidSymbolHandleError>(error);
    }

    [Fact]
    public async Task get_members_csharp_handle_is_invalid()
    {
        using var session = Session(WidgetSnapshot());
        var handle = SymbolHandle.Create("csharp", FsProjectId, "FsLib.Widget.Gadget").Format();
        var (_, error) = await Adapter().GetMembersAsync(session, handle);
        Assert.IsType<InvalidSymbolHandleError>(error);
    }

    [Fact]
    public async Task get_members_unknown_project_is_not_found()
    {
        using var session = Session(WidgetSnapshot());
        var handle = SymbolHandle.Create("fsharp", Guid.NewGuid().ToString("D"), "FsLib.Widget.Gadget").Format();
        var (_, error) = await Adapter().GetMembersAsync(session, handle);
        Assert.IsType<SymbolNotFoundError>(error);
    }

    [Fact]
    public async Task get_members_missing_signature_is_not_found()
    {
        using var session = Session(WidgetSnapshot());
        var handle = SymbolHandle.Create("fsharp", FsProjectId, "FsLib.Gone").Format();
        var (_, error) = await Adapter().GetMembersAsync(session, handle);
        Assert.IsType<SymbolNotFoundError>(error);
    }

    [Fact]
    public async Task get_members_non_container_handle_is_not_found()
    {
        using var session = Session(WidgetSnapshot());
        var adapter = Adapter();
        var (resolved, _) = await adapter.ResolveByNameAsync(session, "ping");
        Assert.NotNull(resolved);

        var (_, error) = await adapter.GetMembersAsync(session, resolved!.Handle);
        Assert.IsType<SymbolNotFoundError>(error);
    }

    [Fact]
    public async Task get_members_bad_cursor_is_stale()
    {
        using var session = Session(WidgetSnapshot());
        var adapter = Adapter();
        var (resolved, _) = await adapter.ResolveByNameAsync(session, "Gadget");
        var (_, error) = await adapter.GetMembersAsync(session, resolved!.Handle, cursor: "not-a-cursor");
        Assert.IsType<StaleCursorError>(error);
    }

    [Fact]
    public async Task get_members_wrong_epoch_cursor_is_stale()
    {
        using var session = Session(WidgetSnapshot(), epoch: 1);
        var adapter = Adapter();
        var (resolved, _) = await adapter.ResolveByNameAsync(session, "Gadget");
        var cursor = MemberPageCursor.Encode(99, 0);
        var (_, error) = await adapter.GetMembersAsync(session, resolved!.Handle, cursor: cursor);
        Assert.IsType<StaleCursorError>(error);
    }

    [Fact]
    public async Task get_members_past_end_cursor_is_stale()
    {
        using var session = Session(WidgetSnapshot(), epoch: 1);
        var adapter = Adapter();
        var (resolved, _) = await adapter.ResolveByNameAsync(session, "Gadget");
        var cursor = MemberPageCursor.Encode(1, 999);
        var (_, error) = await adapter.GetMembersAsync(session, resolved!.Handle, cursor: cursor);
        Assert.IsType<StaleCursorError>(error);
    }

    [Fact]
    public async Task get_project_diagnostics_returns_a_page()
    {
        using var session = Session(BrokenSnapshot());
        var (page, error) = await Adapter().GetProjectDiagnosticsAsync(session, FsProjectId);
        Assert.Null(error);
        Assert.NotNull(page);
        Assert.NotEmpty(page!.Items);
        Assert.All(page.Items, d => Assert.Equal(FsProjectId, d.ProjectId));
    }

    [Fact]
    public async Task get_project_diagnostics_unknown_project_is_not_found()
    {
        using var session = Session(BrokenSnapshot());
        var (_, error) = await Adapter().GetProjectDiagnosticsAsync(session, "missing");
        Assert.IsType<ProjectNotFoundError>(error);
    }

    [Fact]
    public async Task get_project_diagnostics_empty_sources_is_unavailable()
    {
        var snapshot = new FSharpWorkspaceSnapshot(1, [
            new FSharpProjectSnapshot(FsProjectId, "EmptyFs", @"C:\fake-fs-unit\Empty\Empty.fsproj", []),
        ]);
        using var session = Session(snapshot);
        var (_, error) = await Adapter().GetProjectDiagnosticsAsync(session, FsProjectId);
        Assert.IsType<CompilationUnavailableError>(error);
    }

    [Fact]
    public async Task get_project_diagnostics_bad_cursor_is_stale()
    {
        using var session = Session(BrokenSnapshot());
        var (_, error) = await Adapter().GetProjectDiagnosticsAsync(session, FsProjectId, cursor: "not-a-cursor");
        Assert.IsType<StaleCursorError>(error);
    }

    [Fact]
    public async Task get_project_diagnostics_wrong_epoch_cursor_is_stale()
    {
        using var session = Session(BrokenSnapshot(), epoch: 1);
        var cursor = MemberPageCursor.Encode(99, 0);
        var (_, error) = await Adapter().GetProjectDiagnosticsAsync(session, FsProjectId, cursor: cursor);
        Assert.IsType<StaleCursorError>(error);
    }

    [Fact]
    public async Task build_rename_preview_handwritten_succeeds()
    {
        using var session = Session(RenameSnapshot());
        var adapter = Adapter();
        var (resolved, resolveError) = await adapter.ResolveByNameAsync(session, "ping");
        Assert.Null(resolveError);

        var (draft, error) = await adapter.BuildRenamePreviewAsync(session, resolved!.Handle, "pong");
        Assert.Null(error);
        Assert.NotNull(draft);
        Assert.Equal("pong", draft!.NewName);
        Assert.NotEmpty(draft.Documents);
        Assert.Contains(draft.Documents, s => s.NewText.Contains("pong", StringComparison.Ordinal));
    }

    [Fact]
    public async Task build_rename_preview_illegal_name_is_invalid()
    {
        using var session = Session(RenameSnapshot());
        var adapter = Adapter();
        var (resolved, _) = await adapter.ResolveByNameAsync(session, "ping");
        var (_, error) = await adapter.BuildRenamePreviewAsync(session, resolved!.Handle, "A.B");
        Assert.IsType<InvalidRenameNameError>(error);
    }

    [Fact]
    public async Task build_rename_preview_same_name_is_invalid()
    {
        using var session = Session(RenameSnapshot());
        var adapter = Adapter();
        var (resolved, _) = await adapter.ResolveByNameAsync(session, "ping");
        var (_, error) = await adapter.BuildRenamePreviewAsync(session, resolved!.Handle, "ping");
        Assert.IsType<InvalidRenameNameError>(error);
    }

    [Fact]
    public async Task build_rename_preview_unparseable_handle_is_invalid()
    {
        using var session = Session(RenameSnapshot());
        var (_, error) = await Adapter().BuildRenamePreviewAsync(session, "", "pong");
        Assert.IsType<InvalidSymbolHandleError>(error);
    }

    [Fact]
    public async Task build_rename_preview_csharp_handle_is_invalid()
    {
        using var session = Session(RenameSnapshot());
        var handle = SymbolHandle.Create("csharp", FsProjectId, "FsLib.Widget.ping").Format();
        var (_, error) = await Adapter().BuildRenamePreviewAsync(session, handle, "pong");
        Assert.IsType<InvalidSymbolHandleError>(error);
    }

    [Fact]
    public async Task build_rename_preview_unknown_project_is_not_found()
    {
        using var session = Session(RenameSnapshot());
        var handle = SymbolHandle.Create("fsharp", Guid.NewGuid().ToString("D"), "FsLib.Widget.ping").Format();
        var (_, error) = await Adapter().BuildRenamePreviewAsync(session, handle, "pong");
        Assert.IsType<SymbolNotFoundError>(error);
    }

    [Fact]
    public async Task build_rename_preview_missing_signature_is_not_found()
    {
        using var session = Session(RenameSnapshot());
        var handle = SymbolHandle.Create("fsharp", FsProjectId, "FsLib.Gone").Format();
        var (_, error) = await Adapter().BuildRenamePreviewAsync(session, handle, "pong");
        Assert.IsType<SymbolNotFoundError>(error);
    }

    private static FakeSession Session(FSharpWorkspaceSnapshot snapshot, long epoch = 1) =>
        new(snapshot, epoch);

    private static FSharpWorkspaceSnapshot WidgetSnapshot() =>
        new(1, [SnapshotProject(FsProjectId, "FsLib", WidgetPath, WidgetSource)]);

    private static FSharpWorkspaceSnapshot RenameSnapshot() =>
        new(1, [
            new FSharpProjectSnapshot(
                FsProjectId,
                "FsLib",
                @"C:\fake-fs-unit\FsLib\FsLib.fsproj",
                [
                    new FSharpDocumentSnapshot(WidgetPath, WidgetSource),
                    new FSharpDocumentSnapshot(UsesPath, UsesSource),
                ]),
        ]);

    private static FSharpWorkspaceSnapshot BrokenSnapshot() =>
        new(1, [SnapshotProject(FsProjectId, "Broken", BrokenPath, BrokenSource)]);

    private static FSharpProjectSnapshot SnapshotProject(string projectId, string name, string path, string text) =>
        new(
            projectId,
            name,
            Path.ChangeExtension(path, ".fsproj"),
            [new FSharpDocumentSnapshot(path, text)]);

    private sealed class FakeSession : IWorkspaceSession
    {
        private readonly AdhocWorkspace _workspace = new();

        public FakeSession(FSharpWorkspaceSnapshot snapshot, long epoch)
        {
            Solution = _workspace.CurrentSolution;
            Epoch = epoch;
            FSharpSnapshot = snapshot.Epoch == epoch
                ? snapshot
                : new FSharpWorkspaceSnapshot(epoch, snapshot.Projects);
        }

        public long Epoch { get; }

        public Solution Solution { get; }

        public FSharpWorkspaceSnapshot FSharpSnapshot { get; }

        public Task<Compilation> GetCompilationAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException("F# unit tests must not read session.Solution compilation.");

        public Task<Compilation> GetCompilationWithoutGeneratedTreesAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException("F# unit tests must not read session.Solution compilation.");

        public Task<DriverRunSnapshot> GetGeneratorRunResultAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException("F# unit tests must not run generators.");

        public void Dispose() => _workspace.Dispose();
    }
}