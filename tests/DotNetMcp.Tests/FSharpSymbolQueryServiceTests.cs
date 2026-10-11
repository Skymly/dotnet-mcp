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

    private const string HierPath = @"C:\fake-fs-unit\FsHier\Hier.fs";

    private const string HierSource = """
        namespace FsHier

        type IMarker =
            abstract Tag: string

        type Plain() =
            member _.Value = 1

        type MyError() =
            inherit System.Exception()

        type MarkerA() =
            interface IMarker with
                member _.Tag = "a"
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
        var cursor = MemberPageCursor.Encode(99, 0, "symbol_members", resolved!.Handle);
        var (_, error) = await adapter.GetMembersAsync(session, resolved.Handle, cursor: cursor);
        Assert.IsType<StaleCursorError>(error);
    }

    [Fact]
    public async Task get_members_past_end_cursor_is_stale()
    {
        using var session = Session(WidgetSnapshot(), epoch: 1);
        var adapter = Adapter();
        var (resolved, _) = await adapter.ResolveByNameAsync(session, "Gadget");
        var cursor = MemberPageCursor.Encode(1, 999, "symbol_members", resolved!.Handle);
        var (_, error) = await adapter.GetMembersAsync(session, resolved.Handle, cursor: cursor);
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
        var cursor = MemberPageCursor.Encode(99, 0, "project_diagnostics", FsProjectId);
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
        foreach (var bad in new[] { "A.B", "1bad", "a-b", "let", " " })
        {
            var (_, error) = await adapter.BuildRenamePreviewAsync(session, resolved!.Handle, bad);
            Assert.IsType<InvalidRenameNameError>(error);
        }
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

    [Fact]
    public async Task attribution_of_resolved_fsharp_handle_is_language_not_supported()
    {
        using var session = Session(WidgetSnapshot());
        var adapter = Adapter();
        var (resolved, resolveError) = await adapter.ResolveByNameAsync(session, "Gadget");
        Assert.Null(resolveError);
        Assert.NotNull(resolved);
        Assert.StartsWith("fsharp:", resolved!.Handle, StringComparison.Ordinal);

        var (attribution, attrError) = await adapter.GetAttributionAsync(session, resolved.Handle);

        Assert.Null(attribution);
        var unsupported = Assert.IsType<GeneratorLanguageNotSupportedError>(attrError);
        Assert.Equal(SymbolQueryErrorCodes.GeneratorLanguageNotSupported, unsupported.Code);
        Assert.Contains("C#", unsupported.SuggestedAction, StringComparison.Ordinal);
        Assert.Contains("VB", unsupported.SuggestedAction, StringComparison.Ordinal);
        Assert.DoesNotContain("Handwritten", unsupported.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ready workspace", unsupported.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task attribution_of_missing_fsharp_signature_stays_not_found()
    {
        using var session = Session(WidgetSnapshot());
        var handle = SymbolHandle.Create("fsharp", FsProjectId, "FsLib.Gone").Format();
        var (attribution, error) = await Adapter().GetAttributionAsync(session, handle);
        Assert.Null(attribution);
        Assert.IsType<SymbolNotFoundError>(error);
        Assert.NotEqual(SymbolQueryErrorCodes.GeneratorLanguageNotSupported, error!.Code);
    }

    [Fact]
    public async Task members_distinguish_overloaded_ping()
    {
        const string source = """
            module FsLib.Widget

            type Gadget() =
                member _.Ping(x: int) = 1
                member _.Ping(x: string) = 0
            """;
        using var session = Session(new FSharpWorkspaceSnapshot(1, [SnapshotProject(FsProjectId, "FsLib", WidgetPath, source)]));
        var adapter = Adapter();
        var (resolved, resolveError) = await adapter.ResolveByNameAsync(session, "Gadget");
        Assert.Null(resolveError);
        Assert.NotNull(resolved);

        var (members, membersError) = await adapter.GetMembersAsync(session, resolved!.Handle, limit: 50);
        Assert.Null(membersError);
        Assert.NotNull(members);
        var pings = members!.Items.Where(m => m.Summary.DisplayName == "Ping").ToList();
        Assert.True(pings.Count >= 2);
        Assert.Equal(pings.Count, pings.Select(p => p.Handle).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task find_references_and_callers_do_not_mix_overloaded_ping()
    {
        const string source = """
            module FsLib.Widget

            type Gadget() =
                member _.Ping(x: int) = 1
                member _.Ping(x: string) = 0
                member this.UseInt() = this.Ping(1)
                member this.UseStr() = this.Ping("x")
            """;
        using var session = Session(new FSharpWorkspaceSnapshot(1, [SnapshotProject(FsProjectId, "FsLib", WidgetPath, source)]));
        var adapter = Adapter();
        var (resolved, resolveError) = await adapter.ResolveByNameAsync(session, "Gadget");
        Assert.Null(resolveError);
        var (members, membersError) = await adapter.GetMembersAsync(session, resolved!.Handle, limit: 50);
        Assert.Null(membersError);
        var pings = members!.Items.Where(m => m.Summary.DisplayName == "Ping").ToList();
        Assert.Equal(2, pings.Count);
        var intPing = Assert.Single(pings, p => p.Handle.Contains("(System.Int32)", StringComparison.Ordinal));
        var strPing = Assert.Single(pings, p => p.Handle.Contains("(System.String)", StringComparison.Ordinal));

        var (intRefs, intRefError) = await adapter.FindReferencesAsync(session, intPing.Handle);
        Assert.Null(intRefError);
        var (strRefs, strRefError) = await adapter.FindReferencesAsync(session, strPing.Handle);
        Assert.Null(strRefError);

        var intCall = source.IndexOf("this.Ping(1)", StringComparison.Ordinal);
        var strCall = source.IndexOf("this.Ping(\"x\")", StringComparison.Ordinal);
        Assert.True(intCall >= 0);
        Assert.True(strCall >= 0);
        Assert.Contains(intRefs!.Items, r => r.Start is int s && s >= intCall && s < intCall + "this.Ping(1)".Length);
        Assert.DoesNotContain(intRefs.Items, r => r.Start is int s && s >= strCall && s < strCall + "this.Ping(\"x\")".Length);
        Assert.Contains(strRefs!.Items, r => r.Start is int s && s >= strCall && s < strCall + "this.Ping(\"x\")".Length);
        Assert.DoesNotContain(strRefs.Items, r => r.Start is int s && s >= intCall && s < intCall + "this.Ping(1)".Length);

        var (intCallers, intCallerError) = await adapter.FindCallersAsync(session, intPing.Handle);
        Assert.Null(intCallerError);
        var (strCallers, strCallerError) = await adapter.FindCallersAsync(session, strPing.Handle);
        Assert.Null(strCallerError);
        Assert.DoesNotContain(intCallers!.Items, c => c.CallerSummary.DisplayName == "UseStr");
        Assert.DoesNotContain(strCallers!.Items, c => c.CallerSummary.DisplayName == "UseInt");
    }

    [Fact]
    public async Task find_references_message_discloses_defining_project_scope_both_modes()
    {
        using var session = Session(RenameSnapshot());
        var adapter = Adapter();
        var (resolved, resolveError) = await adapter.ResolveByNameAsync(session, "ping");
        Assert.Null(resolveError);

        var (scoped, scopedError) = await adapter.FindReferencesAsync(session, resolved!.Handle);
        Assert.Null(scopedError);
        Assert.Contains("F# search covers only the defining project", scoped!.Message, StringComparison.Ordinal);

        var (entire, entireError) = await adapter.FindReferencesAsync(session, resolved.Handle, entireSolution: true);
        Assert.Null(entireError);
        Assert.Contains("entireSolution does not widen F# search", entire!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task find_callers_message_discloses_defining_project_scope_both_modes()
    {
        using var session = Session(RenameSnapshot());
        var adapter = Adapter();
        var (resolved, resolveError) = await adapter.ResolveByNameAsync(session, "ping");
        Assert.Null(resolveError);

        var (scoped, scopedError) = await adapter.FindCallersAsync(session, resolved!.Handle);
        Assert.Null(scopedError);
        Assert.Contains("F# search covers only the defining project", scoped!.Message, StringComparison.Ordinal);

        var (entire, entireError) = await adapter.FindCallersAsync(session, resolved.Handle, entireSolution: true);
        Assert.Null(entireError);
        Assert.Contains("entireSolution does not widen F# search", entire!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task type_hierarchy_names_external_base_type_instead_of_no_bases()
    {
        using var session = Session(HierSnapshot());
        var adapter = Adapter();
        var (resolved, resolveError) = await adapter.ResolveByNameAsync(session, "MyError");
        Assert.Null(resolveError);
        Assert.NotNull(resolved);

        var (page, error) = await adapter.GetTypeHierarchyAsync(session, resolved!.Handle);
        Assert.Null(error);
        Assert.NotNull(page);
        Assert.Empty(page!.Items);
        Assert.Contains("System.Exception", page.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("has no base types", page.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task type_hierarchy_plain_type_keeps_no_bases_message()
    {
        using var session = Session(HierSnapshot());
        var adapter = Adapter();
        var (resolved, resolveError) = await adapter.ResolveByNameAsync(session, "Plain");
        Assert.Null(resolveError);
        Assert.NotNull(resolved);

        var (page, error) = await adapter.GetTypeHierarchyAsync(session, resolved!.Handle);
        Assert.Null(error);
        Assert.NotNull(page);
        Assert.Empty(page!.Items);
        Assert.Equal("Type has no base types or interfaces.", page.Message);
    }

    [Fact]
    public async Task find_implementations_message_discloses_defining_project_scope()
    {
        using var session = Session(HierSnapshot());
        var adapter = Adapter();
        var (iface, resolveError) = await adapter.ResolveByNameAsync(session, "IMarker");
        Assert.Null(resolveError);
        Assert.NotNull(iface);

        var (page, error) = await adapter.FindImplementationsAsync(session, iface!.Handle);
        Assert.Null(error);
        Assert.NotNull(page);
        Assert.Contains(page!.Items, i => i.Summary.DisplayName == "MarkerA");
        Assert.Contains("F# search covers only types in the defining project.", page.Message, StringComparison.Ordinal);

        var (plain, plainError) = await adapter.ResolveByNameAsync(session, "Plain");
        Assert.Null(plainError);
        var (empty, emptyError) = await adapter.FindImplementationsAsync(session, plain!.Handle);
        Assert.Null(emptyError);
        Assert.NotNull(empty);
        Assert.Empty(empty!.Items);
        Assert.Equal(
            "No implementations were found in the defining F# project; other projects were not searched.",
            empty.Message);
    }

    [Fact]
    public async Task overloaded_member_signatures_are_fully_qualified_and_line_free()
    {
        using var session = Session(OverloadSnapshot());
        var adapter = Adapter();
        var (resolved, resolveError) = await adapter.ResolveByNameAsync(session, "Gadget");
        Assert.Null(resolveError);
        Assert.NotNull(resolved);

        var (members, membersError) = await adapter.GetMembersAsync(session, resolved!.Handle, limit: 50);
        Assert.Null(membersError);
        Assert.NotNull(members);

        var ms = members!.Items.Where(m => m.Summary.DisplayName == "M").ToList();
        Assert.Equal(2, ms.Count);
        var aM = Assert.Single(ms, m => m.Handle.Contains("(NsA.Foo)", StringComparison.Ordinal));
        var bM = Assert.Single(ms, m => m.Handle.Contains("(NsB.Foo)", StringComparison.Ordinal));
        Assert.NotEqual(aM.Handle, bM.Handle);

        var ns = members.Items.Where(m => m.Summary.DisplayName == "N").ToList();
        Assert.Equal(2, ns.Count);
        var intN = Assert.Single(ns, m =>
            m.Handle.Contains("(Microsoft.FSharp.Collections.FSharpList<System.Int32>)", StringComparison.Ordinal));
        var stringN = Assert.Single(ns, m =>
            m.Handle.Contains("(Microsoft.FSharp.Collections.FSharpList<System.String>)", StringComparison.Ordinal));
        Assert.NotEqual(intN.Handle, stringN.Handle);

        foreach (var item in ms.Concat(ns))
        {
            Assert.True(
                SymbolHandle.TryParse(item.Handle, out var parsed, out _),
                item.Handle);
            Assert.DoesNotContain("@", parsed!.SignatureQualifiedName, StringComparison.Ordinal);
            Assert.DoesNotContain("(Foo)", parsed.SignatureQualifiedName, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task find_references_does_not_mix_namespace_overloads()
    {
        using var session = Session(OverloadSnapshot());
        var adapter = Adapter();
        var (resolved, resolveError) = await adapter.ResolveByNameAsync(session, "Gadget");
        Assert.Null(resolveError);
        var (members, membersError) = await adapter.GetMembersAsync(session, resolved!.Handle, limit: 50);
        Assert.Null(membersError);
        var aM = Assert.Single(members!.Items, m => m.Handle.Contains("(NsA.Foo)", StringComparison.Ordinal));
        var bM = Assert.Single(members.Items, m => m.Handle.Contains("(NsB.Foo)", StringComparison.Ordinal));

        var (aRefs, aError) = await adapter.FindReferencesAsync(session, aM.Handle);
        Assert.Null(aError);
        var (bRefs, bError) = await adapter.FindReferencesAsync(session, bM.Handle);
        Assert.Null(bError);

        var callA = CallsSource.IndexOf("callA", StringComparison.Ordinal);
        var callB = CallsSource.IndexOf("callB", StringComparison.Ordinal);
        Assert.True(callA >= 0);
        Assert.True(callB >= 0);

        var aItems = aRefs!.Items.Where(r => string.Equals(r.FilePath, CallsPath, StringComparison.OrdinalIgnoreCase)).ToList();
        var bItems = bRefs!.Items.Where(r => string.Equals(r.FilePath, CallsPath, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Contains(aItems, r => r.Start is int s && s > callA && s < callB);
        Assert.DoesNotContain(aItems, r => r.Start is int s && s > callB);
        Assert.Contains(bItems, r => r.Start is int s && s > callB);
        Assert.DoesNotContain(bItems, r => r.Start is int s && s > callA && s < callB);
    }

    [Fact]
    public async Task rename_preview_of_namespace_overload_changes_only_that_overload()
    {
        using var session = Session(OverloadSnapshot());
        var adapter = Adapter();
        var (resolved, resolveError) = await adapter.ResolveByNameAsync(session, "Gadget");
        Assert.Null(resolveError);
        var (members, membersError) = await adapter.GetMembersAsync(session, resolved!.Handle, limit: 50);
        Assert.Null(membersError);
        var aM = Assert.Single(members!.Items, m => m.Handle.Contains("(NsA.Foo)", StringComparison.Ordinal));

        var (draft, error) = await adapter.BuildRenamePreviewAsync(session, aM.Handle, "Q");
        Assert.Null(error);
        Assert.NotNull(draft);

        var decl = Assert.Single(draft!.Documents, d =>
            string.Equals(d.Path, OverloadsPath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("member _.Q(x: NsA.Foo)", decl.NewText, StringComparison.Ordinal);
        Assert.Contains("member _.M(x: NsB.Foo)", decl.NewText, StringComparison.Ordinal);

        var calls = Assert.Single(draft.Documents, d =>
            string.Equals(d.Path, CallsPath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("gadget.Q(NsA.Foo())", calls.NewText, StringComparison.Ordinal);
        Assert.Contains("gadget.M(NsB.Foo())", calls.NewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task member_signatures_format_functions_tuples_arrays_and_generic_parameters()
    {
        using var session = Session(OverloadSnapshot());
        var adapter = Adapter();
        var (resolved, resolveError) = await adapter.ResolveByNameAsync(session, "Gadget");
        Assert.Null(resolveError);
        var (members, membersError) = await adapter.GetMembersAsync(session, resolved!.Handle, limit: 50);
        Assert.Null(membersError);
        Assert.NotNull(members);

        string Signature(string name)
        {
            var item = Assert.Single(members!.Items, m => m.Summary.DisplayName == name);
            Assert.True(SymbolHandle.TryParse(item.Handle, out var parsed, out _), item.Handle);
            return parsed!.SignatureQualifiedName;
        }

        Assert.EndsWith("P(System.Int32)", Signature("P"), StringComparison.Ordinal);
        Assert.EndsWith("Q(System.String)", Signature("Q"), StringComparison.Ordinal);
        Assert.EndsWith("F(System.Int32->System.String)", Signature("F"), StringComparison.Ordinal);
        Assert.EndsWith("T(System.Int32*System.String)", Signature("T"), StringComparison.Ordinal);
        Assert.EndsWith("A(System.Int32[])", Signature("A"), StringComparison.Ordinal);
        Assert.EndsWith("U('T)", Signature("U"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task find_references_counts_constructor_definitions_and_calls()
    {
        using var session = Session(KindsSnapshot());
        var adapter = Adapter();
        var members = await WidgetMembersAsync(adapter, session);

        var primaryCtor = Assert.Single(members, i => SignatureOf(i) == "Kinds.Widget(System.Int32)");
        var (primary, primaryError) = await adapter.FindReferencesAsync(session, primaryCtor.Handle);
        Assert.Null(primaryError);
        Assert.Equal(4, primary!.Items.Count);

        var newCtor = Assert.Single(members, i => SignatureOf(i) == "Kinds.Widget()");
        var (explicitNew, newError) = await adapter.FindReferencesAsync(session, newCtor.Handle);
        Assert.Null(newError);
        Assert.Equal(2, explicitNew!.Items.Count);
    }

    [Fact]
    public async Task find_references_counts_property_uses_and_accessor_declarations()
    {
        using var session = Session(KindsSnapshot());
        var adapter = Adapter();
        var members = await WidgetMembersAsync(adapter, session);

        var prop = Assert.Single(members, i => SignatureOf(i) == "Kinds.Widget.Prop()");
        var (propRefs, propError) = await adapter.FindReferencesAsync(session, prop.Handle);
        Assert.Null(propError);
        Assert.Equal(5, propRefs!.Items.Count);

        var count = Assert.Single(members, i => SignatureOf(i) == "Kinds.Widget.Count()");
        var (countRefs, countError) = await adapter.FindReferencesAsync(session, count.Handle);
        Assert.Null(countError);
        Assert.Equal(2, countRefs!.Items.Count);
    }

    [Fact]
    public async Task find_references_counts_static_generic_and_curried_members()
    {
        using var session = Session(KindsSnapshot());
        var adapter = Adapter();
        var members = await WidgetMembersAsync(adapter, session);

        var create = Assert.Single(members, i => SignatureOf(i) == "Kinds.Widget.Create(System.Int32)");
        var (createRefs, createError) = await adapter.FindReferencesAsync(session, create.Handle);
        Assert.Null(createError);
        Assert.Equal(2, createRefs!.Items.Count);

        var generic = Assert.Single(members, i => SignatureOf(i) == "Kinds.Widget.Generic('T)");
        var (genericRefs, genericError) = await adapter.FindReferencesAsync(session, generic.Handle);
        Assert.Null(genericError);
        Assert.Equal(2, genericRefs!.Items.Count);

        var (add, addResolveError) = await adapter.ResolveByNameAsync(session, "add");
        Assert.Null(addResolveError);
        var (addRefs, addError) = await adapter.FindReferencesAsync(session, add!.Handle);
        Assert.Null(addError);
        Assert.Equal(2, addRefs!.Items.Count);
    }

    [Fact]
    public async Task rename_preview_of_property_renames_accessors_and_uses()
    {
        using var session = Session(KindsSnapshot());
        var adapter = Adapter();
        var members = await WidgetMembersAsync(adapter, session);
        var prop = Assert.Single(members, i => SignatureOf(i) == "Kinds.Widget.Prop()");

        var (draft, error) = await adapter.BuildRenamePreviewAsync(session, prop.Handle, "Name");
        Assert.Null(error);
        Assert.NotNull(draft);

        var decl = Assert.Single(draft!.Documents, d =>
            string.Equals(d.Path, KindsPath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("member x.Name", decl.NewText, StringComparison.Ordinal);

        var uses = Assert.Single(draft.Documents, d =>
            string.Equals(d.Path, KindsUsesPath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("w0.Name", uses.NewText, StringComparison.Ordinal);
        Assert.Contains("w0.Name <- 9", uses.NewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task check_does_not_notify_unchanged_snapshot_files()
    {
        using var session = Session(WidgetSnapshot());
        var adapter = Adapter();
        var (first, firstError) = await adapter.ResolveByNameAsync(session, "Gadget");
        Assert.Null(firstError);
        Assert.NotNull(first);
        var notified = adapter.FileChangeNotifications;
        Assert.True(notified > 0);
        var (second, secondError) = await adapter.ResolveByNameAsync(session, "Gadget");
        Assert.Null(secondError);
        Assert.NotNull(second);
        Assert.Equal(notified, adapter.FileChangeNotifications);
    }

    [Fact]
    public async Task check_notifies_when_snapshot_text_changes()
    {
        var adapter = Adapter();
        using (var session = Session(WidgetSnapshot()))
        {
            var (_, error) = await adapter.ResolveByNameAsync(session, "Gadget");
            Assert.Null(error);
        }

        var before = adapter.FileChangeNotifications;
        var changed = WidgetSource + "\nlet zoom () = 3\n";
        using var next = Session(new FSharpWorkspaceSnapshot(1, [SnapshotProject(FsProjectId, "FsLib", WidgetPath, changed)]));
        var (resolved, resolveError) = await adapter.ResolveByNameAsync(next, "zoom");
        Assert.Null(resolveError);
        Assert.NotNull(resolved);
        Assert.True(adapter.FileChangeNotifications > before);
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

    private static FSharpWorkspaceSnapshot HierSnapshot() =>
        new(1, [SnapshotProject(FsProjectId, "FsHier", HierPath, HierSource)]);

    private const string OverloadsPath = @"C:\fake-fs-unit\FsOverload\Overloads.fs";
    private const string CallsPath = @"C:\fake-fs-unit\FsOverload\Calls.fs";

    private const string OverloadsSource = """
        namespace NsA

        type Foo() = member _.Tag = "a"

        namespace NsB

        type Foo() = member _.Tag = "b"

        namespace NsC

        type Gadget() =
            member _.M(x: NsA.Foo) = 1
            member _.M(x: NsB.Foo) = 2
            member _.N(x: int list) = 3
            member _.N(x: string list) = 4
            member _.P(x: int) = 5
            member _.Q(x: string) = 6
            member _.F(f: int -> string) = 7
            member _.T(t: int * string) = 8
            member _.A(a: int[]) = 9
            member _.U(x: 'T) = x
        """;

    private const string CallsSource = """
        module NsC.Calls

        let private gadget = NsC.Gadget()

        let callA () = gadget.M(NsA.Foo())
        let callB () = gadget.M(NsB.Foo())
        let callInts () = gadget.N([1])
        let callStrings () = gadget.N(["s"])
        """;

    private const string KindsPath = @"C:\fake-fs-unit\FsKinds\Kinds.fs";
    private const string KindsUsesPath = @"C:\fake-fs-unit\FsKinds\Uses.fs";

    private const string KindsSource = """
        namespace Kinds

        type Widget(input: int) =
            new() = Widget(0)
            member _.Count = input
            member x.Prop
                with get () = input + x.Count
                and set (_v: int) = ()
            member _.Generic<'T>(x: 'T) = x
            static member Create(i: int) = Widget(i)
        """;

    private const string KindsUsesSource = """
        module Kinds.Uses

        let private w0 = Widget(3)
        let private w1 = new Kinds.Widget()
        let private w2 = Kinds.Widget.Create(7)
        let readProp () = w0.Prop
        let writeProp () = w0.Prop <- 9
        let callGeneric () = w0.Generic("hi")
        let add (a: int) (b: int) = a + b
        let apply () = add 1 2
        """;

    private static FSharpWorkspaceSnapshot KindsSnapshot() =>
        new(1, [
            new FSharpProjectSnapshot(
                FsProjectId,
                "FsKinds",
                @"C:\fake-fs-unit\FsKinds\FsKinds.fsproj",
                [
                    new FSharpDocumentSnapshot(KindsPath, KindsSource),
                    new FSharpDocumentSnapshot(KindsUsesPath, KindsUsesSource),
                ]),
        ]);

    private static FSharpWorkspaceSnapshot OverloadSnapshot() =>
        new(1, [
            new FSharpProjectSnapshot(
                FsProjectId,
                "FsOverload",
                @"C:\fake-fs-unit\FsOverload\FsOverload.fsproj",
                [
                    new FSharpDocumentSnapshot(OverloadsPath, OverloadsSource),
                    new FSharpDocumentSnapshot(CallsPath, CallsSource),
                ]),
        ]);

    private static async Task<IReadOnlyList<MemberListItem>> WidgetMembersAsync(
        FSharpSymbolQueryService adapter, FakeSession session)
    {
        var (resolved, resolveError) = await adapter.ResolveByNameAsync(session, "Kinds.Widget");
        Assert.Null(resolveError);
        Assert.NotNull(resolved);
        var (members, membersError) = await adapter.GetMembersAsync(session, resolved!.Handle, limit: 50);
        Assert.Null(membersError);
        Assert.NotNull(members);
        return members!.Items;
    }

    private static string SignatureOf(MemberListItem item)
    {
        Assert.True(SymbolHandle.TryParse(item.Handle, out var parsed, out _), item.Handle);
        return parsed!.SignatureQualifiedName;
    }

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
