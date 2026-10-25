using System.Text;
using System.Text.Json;
using DotNetMcp.Core;
using DotNetMcp.Server;
using DotNetMcp.Xaml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

public class XamlUsageReferenceTests
{
    private const string AxamlPath = @"C:\fake-xaml-refs\MainWindow.axaml";

    private const string AxamlAll = """
        <Window xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:local="using:SampleApp"
                xmlns:c="clr-namespace:SampleControls"
                x:Class="SampleApp.MainWindow"
                x:DataType="local:Customer"
                HandwrittenTitle="hi">
            <c:FancyButton x:Name="SaveButton"
                           Label="{Binding Name}"
                           DataContext="{x:Type local:Address}"
                           Click="OnSave">
                <c:FancyButton.Content>
                    <TextBlock Text="{Binding Home.City}" Tag="{Binding}" />
                </c:FancyButton.Content>
            </c:FancyButton>
            <TextBlock Text="{Binding Title, ElementName=SaveButton}" />
        </Window>
        """;

    private static LanguageAdapters Adapters()
    {
        var roslyn = new RoslynLanguageAdapter(new GeneratorQueryService());
        return new LanguageAdapters([roslyn]);
    }

    private static XamlUsageIndex Index()
    {
        var roslyn = new RoslynLanguageAdapter(new GeneratorQueryService());
        var languages = new LanguageAdapters([roslyn]);
        return new XamlUsageIndex(new XamlDocumentService(languages, roslyn), languages, roslyn);
    }

    private static async Task<string> ResolveAsync(
        LanguageAdapters service, IWorkspaceSession session, string name, string? projectId = null)
    {
        var (resolved, error) = await service.ResolveByNameAsync(session, name, projectId);
        Assert.Null(error);
        Assert.NotNull(resolved);
        return resolved!.Handle;
    }

    [Fact]
    public async Task vm_property_reference_includes_exact_binding_segment_and_excludes_unverified()
    {
        using var workspace = AvaloniaRefsWorkspace();
        await using var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        var service = Adapters();
        using var session = new WorkspaceSession(loaded, epoch: 1, xamlUsageSource: Index());

        var nameHandle = await ResolveAsync(service, session, "SampleApp.Customer.Name");
        var (page, error) = await service.FindReferencesAsync(session, nameHandle, limit: 50);
        Assert.Null(error);
        Assert.NotNull(page);

        var segment = Assert.Single(page!.Items, i => i.XamlUsage == XamlUsageKind.BindingSegment);
        Assert.Equal("Name", AxamlAll.Substring(segment.Start!.Value, segment.Length!.Value));
        Assert.Equal(AxamlPath, segment.FilePath);
        Assert.Equal(ReferenceLocationKind.Reference, segment.Kind);
        AssertQuad(segment, SourceText.From(AxamlAll));

        AssertSourceBeforeXaml(page.Items);

        var titleHandle = await ResolveAsync(service, session, "SampleApp.Vm.Title");
        var (unverifiedPage, unverifiedError) = await service.FindReferencesAsync(session, titleHandle, limit: 50);
        Assert.Null(unverifiedError);
        Assert.NotNull(unverifiedPage);
        Assert.All(unverifiedPage!.Items, i => Assert.Null(i.XamlUsage));
    }

    [Fact]
    public async Task type_references_include_xclass_element_type_and_xdatatype()
    {
        using var workspace = AvaloniaRefsWorkspace();
        await using var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        var service = Adapters();
        using var session = new WorkspaceSession(loaded, epoch: 1, xamlUsageSource: Index());

        var windowHandle = await ResolveAsync(service, session, "SampleApp.MainWindow");
        var (page, error) = await service.FindReferencesAsync(session, windowHandle, limit: 50);
        Assert.Null(error);
        Assert.Single(page!.Items, i => i.XamlUsage == XamlUsageKind.XClass);

        var elementHandle = await ResolveAsync(service, session, "SampleControls.Window");
        (page, error) = await service.FindReferencesAsync(session, elementHandle, limit: 50);
        Assert.Null(error);
        Assert.Equal(2, page!.Items.Count(i => i.XamlUsage == XamlUsageKind.ElementType));

        var customerHandle = await ResolveAsync(service, session, "SampleApp.Customer");
        (page, error) = await service.FindReferencesAsync(session, customerHandle, limit: 50);
        Assert.Null(error);
        Assert.Single(page!.Items, i => i.XamlUsage == XamlUsageKind.XDataType);
        // The self-referencing {Binding} segment also targets the x:DataType.
        Assert.Single(page.Items, i => i.XamlUsage == XamlUsageKind.BindingSegment);
        AssertSourceBeforeXaml(page.Items);
    }

    [Fact]
    public async Task event_handler_method_reference_includes_event_handler_usage()
    {
        using var workspace = AvaloniaRefsWorkspace();
        await using var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        var service = Adapters();
        using var session = new WorkspaceSession(loaded, epoch: 1, xamlUsageSource: Index());

        var handle = await ResolveAsync(service, session, "SampleApp.MainWindow.OnSave()");
        var (page, error) = await service.FindReferencesAsync(session, handle, limit: 50);
        Assert.Null(error);
        var usage = Assert.Single(page!.Items, i => i.XamlUsage == XamlUsageKind.EventHandler);
        Assert.Equal("OnSave", AxamlAll.Substring(usage.Start!.Value, usage.Length!.Value));
    }

    [Fact]
    public async Task xname_generated_field_reference_includes_xname_usage()
    {
        using var workspace = AvaloniaRefsWorkspace();
        await using var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        var service = Adapters();
        using var session = new WorkspaceSession(loaded, epoch: 1, xamlUsageSource: Index());

        var handle = await ResolveAsync(service, session, "SampleApp.MainWindow.SaveButton");
        var (page, error) = await service.FindReferencesAsync(session, handle, limit: 50);
        Assert.Null(error);
        var usage = Assert.Single(page!.Items, i => i.XamlUsage == XamlUsageKind.XName);
        Assert.Equal("SaveButton", AxamlAll.Substring(usage.Start!.Value, usage.Length!.Value));
    }

    [Fact]
    public async Task xaml_reference_items_satisfy_the_unified_position_model()
    {
        using var workspace = AvaloniaRefsWorkspace();
        await using var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        var service = Adapters();
        using var session = new WorkspaceSession(loaded, epoch: 1, xamlUsageSource: Index());
        var text = SourceText.From(AxamlAll);

        var handle = await ResolveAsync(service, session, "SampleControls.FancyButton");
        var (page, error) = await service.FindReferencesAsync(session, handle, limit: 50);
        Assert.Null(error);

        var xamlItems = page!.Items.Where(i => i.XamlUsage is not null).ToArray();
        Assert.Equal(4, xamlItems.Length);
        Assert.All(xamlItems, i =>
        {
            AssertQuad(i, text);
            Assert.Equal(DeclarationAvailability.InSource, i.DeclarationAvailability);
            Assert.Equal(ReferenceLocationKind.Reference, i.Kind);
        });
    }

    [Fact]
    public async Task session_without_xaml_source_returns_source_hits_only()
    {
        using var workspace = AvaloniaRefsWorkspace();
        await using var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        var service = Adapters();

        // Provider wired but null.
        using var unwired = new WorkspaceSession(loaded, epoch: 1);
        var handle = await ResolveAsync(service, unwired, "SampleApp.Customer.Name");
        var (page, error) = await service.FindReferencesAsync(unwired, handle, limit: 50);
        Assert.Null(error);
        Assert.All(page!.Items, i => Assert.Null(i.XamlUsage));
        Assert.DoesNotContain(page.Items, i =>
            (i.FilePath ?? string.Empty).EndsWith(".axaml", StringComparison.OrdinalIgnoreCase));

        // Session that does not implement the provider capability at all.
        using var bare = new BareSession(workspace);
        (page, error) = await service.FindReferencesAsync(bare, handle, limit: 50);
        Assert.Null(error);
        Assert.All(page!.Items, i => Assert.Null(i.XamlUsage));
    }

    [Fact]
    public async Task paging_across_source_to_xaml_boundary_is_exact_and_stage_aware()
    {
        using var workspace = MultiProjectRefsWorkspace(out var libId, out var appId, out _);
        await using var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        var service = Adapters();
        var libGuid = libId.Id.ToString("D");
        var appGuid = appId.Id.ToString("D");
        var source = new StubXamlUsageSource(new Dictionary<string, IReadOnlyList<XamlUsagePoint>>
        {
            [appGuid] =
            [
                Usage(@"C:\fake-refs\B.axaml", 10, 4, appGuid, XamlUsageKind.EventHandler, "SampleLib.Widget.Ping()"),
                Usage(@"C:\fake-refs\B.axaml", 20, 4, appGuid, XamlUsageKind.EventHandler, "SampleLib.Widget.Ping()"),
                Usage(@"C:\fake-refs\B.axaml", 30, 4, appGuid, XamlUsageKind.EventHandler, "SampleLib.Widget.Ping()"),
            ],
        });
        using var session = new WorkspaceSession(loaded, epoch: 1, xamlUsageSource: source);

        var handle = await ResolveAsync(service, session, "SampleLib.Widget.Ping()", libGuid);
        var (first, firstError) = await service.FindReferencesAsync(session, handle, limit: 2);
        Assert.Null(firstError);
        Assert.NotNull(first);
        Assert.Equal(2, first!.Items.Count);
        Assert.True(first.Truncated);
        Assert.False(string.IsNullOrWhiteSpace(first.NextCursor));

        // The declaration fills the first slot; the first XAML hit joins the
        // same page behind it.
        Assert.Null(first.Items[0].XamlUsage);
        Assert.Equal(XamlUsageKind.EventHandler, first.Items[1].XamlUsage);

        Assert.True(FindRefsPageCursor.TryDecode(
            first.NextCursor, out _, out _, out var docIndex, out var locOffset,
            out var stage, out var tool, out var queryId, out var decodeError));
        Assert.Null(decodeError);
        Assert.Equal(1, stage);
        Assert.Equal(0, docIndex);
        Assert.Equal(1, locOffset);
        Assert.Equal("symbol_find_references", tool);
        Assert.Equal(handle, queryId);

        var (second, secondError) = await service.FindReferencesAsync(
            session, handle, limit: 2, cursor: first.NextCursor);
        Assert.Null(secondError);
        Assert.NotNull(second);
        Assert.False(second!.Truncated);
        Assert.Null(second.NextCursor);
        Assert.Equal(2, second.Items.Count);
        Assert.All(second.Items, i => Assert.Equal(XamlUsageKind.EventHandler, i.XamlUsage));

        var all = first.Items.Concat(second.Items).Select(KeyOf).ToArray();
        Assert.Equal(all.Length, all.Distinct().Count());
    }

    [Fact]
    public async Task default_scope_excludes_unrelated_project_xaml_but_entire_solution_includes_it()
    {
        using var workspace = MultiProjectRefsWorkspace(out var libId, out var appId, out var outsiderId);
        await using var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        var service = Adapters();
        var libGuid = libId.Id.ToString("D");
        var appGuid = appId.Id.ToString("D");
        var outsiderGuid = outsiderId.Id.ToString("D");
        var source = new StubXamlUsageSource(new Dictionary<string, IReadOnlyList<XamlUsagePoint>>
        {
            [libGuid] = [Usage(@"C:\fake-refs\Lib.axaml", 10, 4, libGuid, XamlUsageKind.BindingSegment, "SampleLib.Widget.Name")],
            [appGuid] = [Usage(@"C:\fake-refs\App.axaml", 10, 4, appGuid, XamlUsageKind.BindingSegment, "SampleLib.Widget.Name")],
            [outsiderGuid] = [Usage(@"C:\fake-refs\Out.axaml", 10, 4, outsiderGuid, XamlUsageKind.BindingSegment, "SampleLib.Widget.Name")],
        });
        using var session = new WorkspaceSession(loaded, epoch: 1, xamlUsageSource: source);

        var handle = await ResolveAsync(service, session, "SampleLib.Widget.Name", libGuid);

        var (scoped, scopedError) = await service.FindReferencesAsync(session, handle, limit: 50);
        Assert.Null(scopedError);
        var scopedXaml = scoped!.Items.Where(i => i.XamlUsage is not null).ToArray();
        Assert.Equal(2, scopedXaml.Length);
        Assert.DoesNotContain(scopedXaml, i => i.FilePath!.Contains("Out.axaml", StringComparison.OrdinalIgnoreCase));

        var (entire, entireError) = await service.FindReferencesAsync(
            session, handle, entireSolution: true, limit: 50);
        Assert.Null(entireError);
        Assert.Equal(3, entire!.Items.Count(i => i.XamlUsage is not null));
        Assert.Contains(entire.Items, i =>
            i.XamlUsage is not null &&
            i.FilePath!.Contains("Out.axaml", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task shared_xaml_document_seen_by_two_projects_reports_one_usage()
    {
        using var workspace = MultiProjectRefsWorkspace(out var libId, out var appId, out _);
        await using var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        var service = Adapters();
        var libGuid = libId.Id.ToString("D");
        var appGuid = appId.Id.ToString("D");
        const string shared = @"C:\fake-refs\Shared.axaml";
        var source = new StubXamlUsageSource(new Dictionary<string, IReadOnlyList<XamlUsagePoint>>
        {
            [libGuid] = [Usage(shared, 10, 4, libGuid, XamlUsageKind.BindingSegment, "SampleLib.Widget.Name")],
            [appGuid] = [Usage(shared, 10, 4, appGuid, XamlUsageKind.BindingSegment, "SampleLib.Widget.Name")],
        });
        using var session = new WorkspaceSession(loaded, epoch: 1, xamlUsageSource: source);

        var handle = await ResolveAsync(service, session, "SampleLib.Widget.Name", libId.Id.ToString("D"));
        var (page, error) = await service.FindReferencesAsync(session, handle, limit: 50);
        Assert.Null(error);
        Assert.Single(page!.Items, i => i.XamlUsage == XamlUsageKind.BindingSegment);
    }

    [Fact]
    public async Task stage1_cursor_rejects_wrong_tool_wrong_handle_and_scope_flip()
    {
        using var workspace = MultiProjectRefsWorkspace(out var libId, out var appId, out _);
        await using var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        var service = Adapters();
        var libGuid = libId.Id.ToString("D");
        var appGuid = appId.Id.ToString("D");
        var source = new StubXamlUsageSource(new Dictionary<string, IReadOnlyList<XamlUsagePoint>>
        {
            [appGuid] =
            [
                Usage(@"C:\fake-refs\B.axaml", 10, 4, appGuid, XamlUsageKind.EventHandler, "SampleLib.Widget.Ping()"),
                Usage(@"C:\fake-refs\B.axaml", 20, 4, appGuid, XamlUsageKind.EventHandler, "SampleLib.Widget.Ping()"),
            ],
        });
        using var session = new WorkspaceSession(loaded, epoch: 1, xamlUsageSource: source);

        var handle = await ResolveAsync(service, session, "SampleLib.Widget.Ping()", libGuid);
        var (first, firstError) = await service.FindReferencesAsync(session, handle, limit: 2);
        Assert.Null(firstError);
        Assert.True(FindRefsPageCursor.TryDecode(
            first!.NextCursor, out _, out _, out _, out _, out var stage, out _, out _, out _));
        Assert.Equal(1, stage);

        // Wrong handle: cursor identity is bound to the query.
        var otherHandle = await ResolveAsync(service, session, "SampleLib.Widget.Name", libGuid);
        var (_, wrongHandle) = await service.FindReferencesAsync(
            session, otherHandle, limit: 2, cursor: first.NextCursor);
        Assert.IsType<StaleCursorError>(wrongHandle);

        // Wrong tool: the same cursor must not resume find_callers even though
        // the query handle matches.
        var (_, wrongTool) = await service.FindCallersAsync(
            session, handle, limit: 2, cursor: first.NextCursor);
        Assert.IsType<StaleCursorError>(wrongTool);

        // Scope flip: a scoped cursor cannot continue an entire-solution walk.
        var (_, wrongScope) = await service.FindReferencesAsync(
            session, handle, entireSolution: true, limit: 2, cursor: first.NextCursor);
        Assert.IsType<StaleCursorError>(wrongScope);
    }

    [Fact]
    public async Task v4_cursor_payload_is_stale()
    {
        using var workspace = MultiProjectRefsWorkspace(out var libId, out _, out _);
        await using var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        var service = Adapters();
        using var session = new WorkspaceSession(
            loaded, epoch: 1,
            xamlUsageSource: new StubXamlUsageSource(new Dictionary<string, IReadOnlyList<XamlUsagePoint>>()));

        var handle = await ResolveAsync(service, session, "SampleLib.Widget.Name", libId.Id.ToString("D"));
        var json = JsonSerializer.Serialize(new
        {
            V = "v4",
            Epoch = 1L,
            EntireSolution = false,
            DocIndex = 0,
            LocOffset = 0,
            IssuedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Tool = "symbol_find_references",
            QueryId = handle
        });
        var cursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        var (_, error) = await service.FindReferencesAsync(session, handle, cursor: cursor);
        Assert.IsType<StaleCursorError>(error);
    }

    [Fact]
    public async Task soft_budget_zero_truncates_source_stage_and_stage1_resume_makes_progress()
    {
        using var workspace = MultiProjectRefsWorkspace(out var libId, out var appId, out var outsiderId);
        await using var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        var service = Adapters();

        // Deterministic collection order: scoped projects walk in ProjectId order.
        var ordered = new[] { libId, appId, outsiderId }
            .OrderBy(id => id.Id.ToString("D"), StringComparer.Ordinal)
            .ToArray();
        var firstGuid = ordered[0].Id.ToString("D");
        var secondGuid = ordered[1].Id.ToString("D");
        var thirdGuid = ordered[2].Id.ToString("D");

        // The second-collected project's file sorts *before* the first's, so a
        // global path ordering would drop or duplicate hits across the
        // budget-truncated boundary; project-major order stays prefix-stable.
        var source = new StubXamlUsageSource(new Dictionary<string, IReadOnlyList<XamlUsagePoint>>
        {
            [firstGuid] =
            [
                Usage(@"C:\fake-refs\B.axaml", 10, 4, firstGuid, XamlUsageKind.BindingSegment, "SampleLib.Widget.Name"),
                Usage(@"C:\fake-refs\B.axaml", 20, 4, firstGuid, XamlUsageKind.BindingSegment, "SampleLib.Widget.Name"),
            ],
            [secondGuid] =
            [
                Usage(@"C:\fake-refs\A.axaml", 10, 4, secondGuid, XamlUsageKind.BindingSegment, "SampleLib.Widget.Name"),
            ],
            [thirdGuid] = [],
        });
        using var session = new WorkspaceSession(loaded, epoch: 1, xamlUsageSource: source);

        var handle = await ResolveAsync(
            service, session, "SampleLib.Widget.Name", libId.Id.ToString("D"));

        // A stage-1 cursor plus an empty budget still completes the project in
        // flight, then cursors — same contract as the document scan finishing
        // its in-flight document. Repeated zero-budget calls keep advancing
        // one unit at a time until the list ends.
        var stage1 = FindRefsPageCursor.Encode(
            session.Epoch, entireSolution: true, docIndex: 0, locOffset: 0,
            tool: "symbol_find_references", queryId: handle, stage: 1);
        var (first, firstError) = await service.FindReferencesAsync(
            session, handle, entireSolution: true, limit: 50, cursor: stage1, softBudget: TimeSpan.Zero);
        Assert.Null(firstError);
        Assert.NotNull(first);
        Assert.True(first!.Truncated);
        Assert.False(string.IsNullOrWhiteSpace(first.NextCursor));
        Assert.All(first.Items, i => Assert.Equal(@"C:\fake-refs\B.axaml", i.FilePath));

        var (second, secondError) = await service.FindReferencesAsync(
            session, handle, entireSolution: true, limit: 50, cursor: first.NextCursor,
            softBudget: TimeSpan.Zero);
        Assert.Null(secondError);
        Assert.NotNull(second);
        Assert.True(second!.Truncated);
        Assert.Single(second.Items);
        Assert.Equal(@"C:\fake-refs\A.axaml", second.Items[0].FilePath);

        var (third, thirdError) = await service.FindReferencesAsync(
            session, handle, entireSolution: true, limit: 50, cursor: second.NextCursor,
            softBudget: TimeSpan.Zero);
        Assert.Null(thirdError);
        Assert.NotNull(third);
        Assert.False(third!.Truncated);
        Assert.Empty(third.Items);

        var all = first.Items.Concat(second.Items).Select(KeyOf).ToArray();
        Assert.Equal(3, all.Distinct().Count());
    }

    [Fact]
    public async Task soft_budget_zero_on_first_call_truncates_inside_source_stage()
    {
        using var workspace = MultiProjectRefsWorkspace(out var libId, out _, out _);
        await using var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        var service = Adapters();
        using var session = new WorkspaceSession(
            loaded, epoch: 1,
            xamlUsageSource: new StubXamlUsageSource(new Dictionary<string, IReadOnlyList<XamlUsagePoint>>()));

        var handle = await ResolveAsync(service, session, "SampleLib.Widget.Name", libId.Id.ToString("D"));
        var (page, error) = await service.FindReferencesAsync(
            session, handle, entireSolution: true, limit: 50, softBudget: TimeSpan.Zero);
        Assert.Null(error);
        Assert.True(page!.Truncated);
        Assert.True(FindRefsPageCursor.TryDecode(
            page.NextCursor, out _, out _, out _, out _, out var stage, out _, out _, out _));
        Assert.Equal(0, stage);
    }

    [Fact]
    public async Task end_to_end_tool_reports_xaml_usage_after_source_hits()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "MainWindow.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(axaml, """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="using:SampleApp"
                    x:Class="SampleApp.MainWindow"
                    x:DataType="local:Customer">
                <TextBlock Text="{Binding Home.City}" />
            </Window>
            """);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithAvalonia());
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "SampleApp.Address.City" });
            Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
            var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;

            var result = await fx.Client.CallToolAsync(
                "symbol_find_references",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
            var body = InProcessMcpFixture.Deserialize<SymbolFindReferencesResultDto>(result);

            var xaml = Assert.Single(body.Items, i => i.XamlUsage == "BindingSegment");
            Assert.Equal(axaml, xaml.FilePath);
            Assert.All(
                body.Items.Where(i => i.XamlUsage is null),
                i => Assert.False(
                    (i.FilePath ?? string.Empty).EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)));

            // Wire shape: the serialized item uses camelCase xamlUsage and
            // source hits omit the field entirely.
            using var doc = JsonDocument.Parse(InProcessMcpFixture.TextOf(result));
            var items = doc.RootElement.GetProperty("items").EnumerateArray().ToArray();
            var xamlItem = Assert.Single(items, i => i.TryGetProperty("xamlUsage", out _));
            Assert.Equal("BindingSegment", xamlItem.GetProperty("xamlUsage").GetString());
            Assert.All(
                items.Where(i => !i.TryGetProperty("xamlUsage", out _)),
                i => Assert.False(
                    i.GetProperty("filePath").GetString()!
                        .EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void AssertSourceBeforeXaml(IReadOnlyList<ReferenceLocationItem> items)
    {
        var firstXaml = items.ToList().FindIndex(i => i.XamlUsage is not null);
        if (firstXaml < 0)
        {
            return;
        }

        Assert.All(items.Take(firstXaml), i => Assert.Null(i.XamlUsage));
    }

    private static void AssertQuad(ReferenceLocationItem item, SourceText text)
    {
        var start = text.Lines.GetPosition(new LinePosition(item.Line!.Value - 1, item.Column!.Value - 1));
        var end = text.Lines.GetPosition(new LinePosition(item.EndLine!.Value - 1, item.EndColumn!.Value - 1));
        Assert.Equal(item.Start!.Value, start);
        Assert.Equal(item.Start.Value + item.Length!.Value, end);
    }

    private static string KeyOf(ReferenceLocationItem item) =>
        $"{item.FilePath}|{item.Start}|{item.Length}|{item.XamlUsage}";

    private static XamlUsagePoint Usage(
        string path,
        int start,
        int length,
        string projectId,
        XamlUsageKind kind,
        string symbolKey,
        XamlUsageConfidence confidence = XamlUsageConfidence.Exact) =>
        new(
            path,
            start,
            length,
            1,
            start + 1,
            1,
            start + length + 1,
            projectId,
            kind,
            confidence,
            symbolKey,
            SymbolHandle.Create(LanguageAdapters.CSharpLanguage, projectId, symbolKey).Format(),
            string.Empty);

    private sealed class StubXamlUsageSource(IReadOnlyDictionary<string, IReadOnlyList<XamlUsagePoint>> byProject)
        : IXamlUsageSource
    {
        public Task<IReadOnlyList<XamlUsagePoint>> GetUsagesAsync(
            IWorkspaceSession session,
            string projectId,
            CancellationToken ct = default) =>
            Task.FromResult(
                byProject.TryGetValue(projectId, out var usages)
                    ? usages
                    : (IReadOnlyList<XamlUsagePoint>)[]);

        public Task<XamlUsagePoint?> FindAtPositionAsync(
            IWorkspaceSession session,
            string path,
            int line,
            int column,
            string? projectId = null,
            CancellationToken ct = default) =>
            Task.FromResult<XamlUsagePoint?>(null);

        public bool IsXamlDocumentPath(string path) =>
            path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Session without <see cref="IXamlUsageSourceProvider"/>: the unwired seam.</summary>
    private sealed class BareSession : IWorkspaceSession
    {
        private readonly GeneratorRunCache _cache = new();

        public BareSession(AdhocWorkspace workspace, long epoch = 1)
        {
            Solution = workspace.CurrentSolution;
            Epoch = epoch;
            FSharpSnapshot = new FSharpWorkspaceSnapshot(epoch, []);
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

    private static AdhocWorkspace MultiProjectRefsWorkspace(
        out ProjectId libId, out ProjectId appId, out ProjectId outsiderId)
    {
        var workspace = new AdhocWorkspace();
        libId = ProjectId.CreateNewId();
        appId = ProjectId.CreateNewId();
        outsiderId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                libId, VersionStamp.Create(), "LibA", "LibA", LanguageNames.CSharp,
                filePath: @"C:\fake-refs\LibA.csproj"))
            .AddProject(ProjectInfo.Create(
                appId, VersionStamp.Create(), "App", "App", LanguageNames.CSharp,
                filePath: @"C:\fake-refs\App.csproj"))
            .AddProject(ProjectInfo.Create(
                outsiderId, VersionStamp.Create(), "Outsider", "Outsider", LanguageNames.CSharp,
                filePath: @"C:\fake-refs\Outsider.csproj"));

        const string libSource = """
            namespace SampleLib
            {
                public class Widget
                {
                    public string Name { get; set; } = "";
                    public void Ping() { }
                }
            }
            """;
        const string appSource = """
            namespace AppNs
            {
                public class Uses
                {
                    public string Read(SampleLib.Widget w) => w.Name;
                }
            }
            """;
        const string outsiderSource = """
            namespace OutsiderNs
            {
                public class Marker { }
            }
            """;

        solution = solution.AddDocument(
            DocumentId.CreateNewId(libId), "Widget.cs", SourceText.From(libSource),
            filePath: @"C:\fake-refs\Widget.cs");
        solution = solution.AddDocument(
            DocumentId.CreateNewId(appId), "Uses.cs", SourceText.From(appSource),
            filePath: @"C:\fake-refs\Uses.cs");
        solution = solution.AddDocument(
            DocumentId.CreateNewId(outsiderId), "Marker.cs", SourceText.From(outsiderSource),
            filePath: @"C:\fake-refs\Marker.cs");
        var runtime = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        solution = solution
            .AddMetadataReference(libId, runtime)
            .AddMetadataReference(appId, runtime)
            .AddMetadataReference(outsiderId, runtime);
        solution = solution
            .WithProjectCompilationOptions(libId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithProjectCompilationOptions(appId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithProjectCompilationOptions(outsiderId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddProjectReference(appId, new ProjectReference(libId));
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply multi-project refs AdhocWorkspace.");
        }

        return workspace;
    }

    private static AdhocWorkspace AvaloniaRefsWorkspace()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var csId = DocumentId.CreateNewId(projectId);
        var xamlId = DocumentId.CreateNewId(projectId);
        const string source = """
            using System;
            using Avalonia.Metadata;

            [assembly: XmlnsDefinition("https://github.com/avaloniaui", "SampleControls")]

            namespace Avalonia.Metadata
            {
                [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
                public sealed class XmlnsDefinitionAttribute : Attribute
                {
                    public XmlnsDefinitionAttribute(string xmlNamespace, string clrNamespace)
                    {
                        XmlNamespace = xmlNamespace;
                        ClrNamespace = clrNamespace;
                    }

                    public string XmlNamespace { get; }
                    public string ClrNamespace { get; }
                }
            }

            namespace SampleApp
            {
                public partial class MainWindow
                {
                    public MainWindow() { }

                    private object SaveButton = new();

                    public void OnSave() { }
                }

                public class Address
                {
                    public string City { get; set; } = "";
                }

                public class Customer
                {
                    public Address Home { get; set; } = new();
                    public string Name { get; set; } = "";
                }

                public class Vm
                {
                    public string Title { get; set; } = "";
                }
            }

            namespace SampleControls
            {
                public class Window
                {
                    public string HandwrittenTitle { get; set; } = "";
                }

                public class FancyButton
                {
                    public string Label { get; set; } = "";

                    public event EventHandler? Click;
                }

                public class TextBlock
                {
                }
            }
            """;
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "AvaloniaApp",
            "AvaloniaApp",
            LanguageNames.CSharp,
            filePath: @"C:\fake-xaml-refs\AvaloniaApp.csproj"));
        solution = solution.AddDocument(
            csId, "MainWindow.axaml.cs", SourceText.From(source),
            filePath: @"C:\fake-xaml-refs\MainWindow.axaml.cs");
        solution = solution.AddAdditionalDocument(
            xamlId, "MainWindow.axaml", SourceText.From(AxamlAll), filePath: AxamlPath);
        solution = solution.WithProjectCompilationOptions(
            projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(
            projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply Avalonia refs AdhocWorkspace.");
        }

        return workspace;
    }

    private static string CreateTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnet-mcp-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
