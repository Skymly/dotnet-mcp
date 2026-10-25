using System.Text;
using System.Text.Json;
using DotNetMcp.Core;
using DotNetMcp.Server;
using DotNetMcp.Xaml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

public class XamlRenameSeamTests
{
    private const string AxamlPath = @"C:\fake-xaml-rename\MainWindow.axaml";

    // Same usage shapes as XamlUsageReferenceTests.AxamlAll so the rename edits
    // are exercised against every Exact kind plus one Unverified segment.
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

    private const string MauiPath = @"C:\fake-xaml-rename\MainPage.xaml";
    private const string MauiAll = """
        <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:local="clr-namespace:MauiPage"
                     x:Class="MauiPage.MainPage"
                     x:DataType="local:MainViewModel">
            <Label x:Name="TitleLabel" Text="{Binding Title}" />
        </ContentPage>
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
        LanguageAdapters service, IWorkspaceSession session, string name, ProjectId? projectId = null)
    {
        var (resolved, error) = await service.ResolveByNameAsync(
            session, name, projectId?.Id.ToString("D"));
        Assert.Null(error);
        Assert.NotNull(resolved);
        return resolved!.Handle;
    }

    private static RenameDocumentSlice AxamlSlice(RenamePreviewDraft draft) =>
        Assert.Single(draft.Documents, d =>
            string.Equals(d.Path, AxamlPath, StringComparison.OrdinalIgnoreCase));

    private static (LoadedSolution Loaded, WorkspaceSession Session) WiredSession(
        AdhocWorkspace workspace, IXamlUsageSource? source = null)
    {
        var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        var session = new WorkspaceSession(loaded, epoch: 1, xamlUsageSource: source ?? Index());
        return (loaded, session);
    }

    [Fact]
    public async Task property_rename_updates_binding_segment_and_property_attribute_only()
    {
        using var workspace = AvaloniaRenameWorkspace();
        var (loaded, session) = WiredSession(workspace);
        await using var _ = loaded;
        using var __ = session;
        var service = Adapters();

        // Compiled-binding segment: only the matching segment is replaced.
        var nameHandle = await ResolveAsync(service, session, "SampleApp.Customer.Name");
        var (draft, error) = await service.BuildRenamePreviewAsync(session, nameHandle, "Nickname");
        Assert.Null(error);
        Assert.NotNull(draft);
        var xaml = AxamlSlice(draft!);
        Assert.Contains("{Binding Nickname}", xaml.NewText, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding Name}", xaml.NewText, StringComparison.Ordinal);
        // Other segments and attributes stay put.
        Assert.Contains("{Binding Home.City}", xaml.NewText, StringComparison.Ordinal);
        Assert.Contains("{Binding Title, ElementName=SaveButton}", xaml.NewText, StringComparison.Ordinal);
        Assert.Contains(draft.Documents, d =>
            d.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
            d.NewText.Contains("Nickname", StringComparison.Ordinal));
        Assert.True(draft.UnverifiedXamlMentions is null || draft.UnverifiedXamlMentions.Count == 0);

        // Property-attribute name: the attribute name is the usage span.
        var attrHandle = await ResolveAsync(service, session, "SampleControls.Window.HandwrittenTitle");
        (draft, error) = await service.BuildRenamePreviewAsync(session, attrHandle, "Heading");
        Assert.Null(error);
        xaml = AxamlSlice(draft!);
        Assert.Contains("Heading=\"hi\"", xaml.NewText, StringComparison.Ordinal);
        Assert.DoesNotContain("HandwrittenTitle=\"hi\"", xaml.NewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task unverified_binding_is_reported_as_mention_and_never_edited()
    {
        using var workspace = AvaloniaRenameWorkspace();
        var (loaded, session) = WiredSession(workspace);
        await using var _ = loaded;
        using var __ = session;
        var service = Adapters();

        // {Binding Title, ElementName=SaveButton} is context-free, so the Title
        // segment matches Vm.Title by name only — Unverified.
        var titleHandle = await ResolveAsync(service, session, "SampleApp.Vm.Title");
        var (draft, error) = await service.BuildRenamePreviewAsync(session, titleHandle, "Heading");
        Assert.Null(error);
        Assert.NotNull(draft);

        var mention = Assert.Single(draft!.UnverifiedXamlMentions!);
        Assert.Equal(AxamlPath, mention.Path);
        Assert.Equal("Title", mention.Text);
        var text = SourceText.From(AxamlAll);
        var start = text.Lines.GetPosition(new LinePosition(mention.Line - 1, mention.Column - 1));
        var end = text.Lines.GetPosition(new LinePosition(mention.EndLine - 1, mention.EndColumn - 1));
        Assert.Equal("Title", AxamlAll[start..end]);

        // The unverified span is not written — no XAML slice for this rename.
        Assert.DoesNotContain(draft.Documents, d =>
            d.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) ||
            d.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase));
        // The handwritten property still renames.
        Assert.Contains(draft.Documents, d =>
            d.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
            d.NewText.Contains("Heading", StringComparison.Ordinal));
    }

    [Fact]
    public async Task type_rename_updates_tags_xclass_xdatatype_and_markup_refs()
    {
        using var workspace = AvaloniaRenameWorkspace();
        var (loaded, session) = WiredSession(workspace);
        await using var _ = loaded;
        using var __ = session;
        var service = Adapters();

        // Element start/end tags plus the property-element prefix owner:
        // all four c:FancyButton occurrences carry the xmlns prefix; only the
        // simple-name tail is replaced.
        var buttonHandle = await ResolveAsync(service, session, "SampleControls.FancyButton");
        var (draft, error) = await service.BuildRenamePreviewAsync(session, buttonHandle, "SuperButton");
        Assert.Null(error);
        var xaml = AxamlSlice(draft!);
        Assert.Contains("<c:SuperButton ", xaml.NewText, StringComparison.Ordinal);
        Assert.Contains("</c:SuperButton>", xaml.NewText, StringComparison.Ordinal);
        Assert.Contains("<c:SuperButton.Content>", xaml.NewText, StringComparison.Ordinal);
        Assert.Contains("</c:SuperButton.Content>", xaml.NewText, StringComparison.Ordinal);
        Assert.DoesNotContain("FancyButton", xaml.NewText, StringComparison.Ordinal);

        // x:Class is CLR-qualified; only the simple-name tail is replaced.
        var windowHandle = await ResolveAsync(service, session, "SampleApp.MainWindow");
        (draft, error) = await service.BuildRenamePreviewAsync(session, windowHandle, "ShellWindow");
        Assert.Null(error);
        xaml = AxamlSlice(draft!);
        Assert.Contains("x:Class=\"SampleApp.ShellWindow\"", xaml.NewText, StringComparison.Ordinal);
        Assert.DoesNotContain("SampleApp.MainWindow", xaml.NewText, StringComparison.Ordinal);
        // xmlns-prefixed usages of other symbols stay put.
        Assert.Contains("x:DataType=\"local:Customer\"", xaml.NewText, StringComparison.Ordinal);

        // x:DataType is xmlns-prefixed; the empty-path {Binding} segment also
        // targets the type but its span cannot carry the simple name, so it is
        // left untouched (the binding still binds to the renamed DataContext).
        var customerHandle = await ResolveAsync(service, session, "SampleApp.Customer");
        (draft, error) = await service.BuildRenamePreviewAsync(session, customerHandle, "Client");
        Assert.Null(error);
        xaml = AxamlSlice(draft!);
        Assert.Contains("x:DataType=\"local:Client\"", xaml.NewText, StringComparison.Ordinal);
        Assert.Contains("Tag=\"{Binding}\"", xaml.NewText, StringComparison.Ordinal);

        // {x:Type local:Address} markup type ref is xmlns-prefixed.
        var addressHandle = await ResolveAsync(service, session, "SampleApp.Address");
        (draft, error) = await service.BuildRenamePreviewAsync(session, addressHandle, "Location");
        Assert.Null(error);
        xaml = AxamlSlice(draft!);
        Assert.Contains("{x:Type local:Location}", xaml.NewText, StringComparison.Ordinal);
        Assert.DoesNotContain("local:Address", xaml.NewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task event_handler_method_rename_updates_attribute_value()
    {
        using var workspace = AvaloniaRenameWorkspace();
        var (loaded, session) = WiredSession(workspace);
        await using var _ = loaded;
        using var __ = session;
        var service = Adapters();

        var handle = await ResolveAsync(service, session, "SampleApp.MainWindow.OnSave()");
        var (draft, error) = await service.BuildRenamePreviewAsync(session, handle, "OnSaved");
        Assert.Null(error);
        var xaml = AxamlSlice(draft!);
        Assert.Contains("Click=\"OnSaved\"", xaml.NewText, StringComparison.Ordinal);
        // The event attribute name resolves to FancyButton.Click — untouched.
        Assert.DoesNotContain("Click=\"OnSave\"", xaml.NewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task xname_field_rename_updates_xaml_value_and_source()
    {
        using var workspace = AvaloniaRenameWorkspace();
        var (loaded, session) = WiredSession(workspace);
        await using var _ = loaded;
        using var __ = session;
        var service = Adapters();

        // SaveButton is a handwritten field, so the ordinary rename path also
        // rewrites the x:Name value — the usage span IS the identifier text.
        var handle = await ResolveAsync(service, session, "SampleApp.MainWindow.SaveButton");
        var (draft, error) = await service.BuildRenamePreviewAsync(session, handle, "ConfirmButton");
        Assert.Null(error);
        var xaml = AxamlSlice(draft!);
        Assert.Contains("x:Name=\"ConfirmButton\"", xaml.NewText, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"SaveButton\"", xaml.NewText, StringComparison.Ordinal);
        // ElementName=SaveButton inside the binding value is not a usage point;
        // the index does not track it, so it is left untouched by design.
        Assert.Contains("ElementName=SaveButton", xaml.NewText, StringComparison.Ordinal);
        var source = Assert.Single(draft!.Documents, d =>
            d.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("ConfirmButton", source.NewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task maui_xaml_joins_property_and_xname_renames()
    {
        using var workspace = MauiRenameWorkspace();
        var (loaded, session) = WiredSession(workspace);
        await using var _ = loaded;
        using var __ = session;
        var service = Adapters();

        // Binding segment under x:DataType.
        var titleHandle = await ResolveAsync(service, session, "MauiPage.MainViewModel.Title");
        var (draft, error) = await service.BuildRenamePreviewAsync(session, titleHandle, "Heading");
        Assert.Null(error);
        var xaml = Assert.Single(draft!.Documents, d =>
            string.Equals(d.Path, MauiPath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Text=\"{Binding Heading}\"", xaml.NewText, StringComparison.Ordinal);

        // Property attribute on the MAUI control.
        var textHandle = await ResolveAsync(service, session, "Microsoft.Maui.Controls.Label.Text");
        (draft, error) = await service.BuildRenamePreviewAsync(session, textHandle, "LabelText");
        Assert.Null(error);
        xaml = Assert.Single(draft!.Documents, d =>
            string.Equals(d.Path, MauiPath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("LabelText=\"{Binding Title}\"", xaml.NewText, StringComparison.Ordinal);

        // x:Name value on a handwritten field.
        var fieldHandle = await ResolveAsync(service, session, "MauiPage.MainPage.TitleLabel");
        (draft, error) = await service.BuildRenamePreviewAsync(session, fieldHandle, "HeaderLabel");
        Assert.Null(error);
        xaml = Assert.Single(draft!.Documents, d =>
            string.Equals(d.Path, MauiPath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("x:Name=\"HeaderLabel\"", xaml.NewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task unwired_sessions_collect_no_xaml_edits_or_mentions()
    {
        using var workspace = AvaloniaRenameWorkspace();
        var service = Adapters();

        // Provider present but source unwired.
        await using var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        using (var session = new WorkspaceSession(loaded, epoch: 1))
        {
            var handle = await ResolveAsync(service, session, "SampleApp.Customer.Name");
            var (draft, error) = await service.BuildRenamePreviewAsync(session, handle, "Nickname");
            Assert.Null(error);
            Assert.NotNull(draft);
            Assert.DoesNotContain(draft!.Documents, d =>
                d.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase));
            Assert.True(draft.UnverifiedXamlMentions is null || draft.UnverifiedXamlMentions.Count == 0);
        }

        // Session without the provider interface at all.
        using var bare = new BareSession(workspace);
        var bareHandle = await ResolveAsync(service, bare, "SampleApp.Customer.Name");
        var (bareDraft, bareError) = await service.BuildRenamePreviewAsync(bare, bareHandle, "Nickname");
        Assert.Null(bareError);
        Assert.NotNull(bareDraft);
        Assert.DoesNotContain(bareDraft!.Documents, d =>
            d.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase));
        Assert.True(bareDraft.UnverifiedXamlMentions is null ||
                    bareDraft.UnverifiedXamlMentions.Count == 0);
        // The source rename still works exactly as before.
        Assert.Contains(bareDraft.Documents, d =>
            d.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
            d.NewText.Contains("Nickname", StringComparison.Ordinal));
    }

    [Fact]
    public async Task stale_or_malformed_usage_spans_fail_closed()
    {
        using var workspace = AvaloniaRenameWorkspace();
        await using var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        var service = Adapters();
        var projectId = workspace.CurrentSolution.Projects.Single().Id.Id.ToString("D");

        // Out-of-range, wrong-text, and foreign-path usages are all skipped.
        var stub = new StubXamlUsageSource(new Dictionary<string, IReadOnlyList<XamlUsagePoint>>
        {
            [projectId] =
            [
                Usage(AxamlPath, AxamlAll.Length + 10, 4, projectId, XamlUsageKind.BindingSegment,
                    "SampleApp.Customer.Name"),
                Usage(AxamlPath, 0, 4, projectId, XamlUsageKind.BindingSegment,
                    "SampleApp.Customer.Name"),
                Usage(@"C:\fake-xaml-rename\NotRegistered.axaml", 0, 4, projectId,
                    XamlUsageKind.BindingSegment, "SampleApp.Customer.Name"),
                Usage(AxamlPath, AxamlAll.IndexOf("Title,", StringComparison.Ordinal), 5, projectId,
                    XamlUsageKind.BindingSegment, "SampleApp.Vm.Title",
                    XamlUsageConfidence.Unverified)
            ]
        });
        using var session = new WorkspaceSession(loaded, epoch: 1, xamlUsageSource: stub);

        var handle = await ResolveAsync(service, session, "SampleApp.Customer.Name");
        var (draft, error) = await service.BuildRenamePreviewAsync(session, handle, "Nickname");
        Assert.Null(error);
        Assert.NotNull(draft);
        // Every malformed Exact usage was skipped — no XAML slice at all.
        Assert.DoesNotContain(draft!.Documents, d =>
            d.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase));
        // An unverified mention for a different symbol is not reported either.
        Assert.True(draft.UnverifiedXamlMentions is null || draft.UnverifiedXamlMentions.Count == 0);
    }

    [Fact]
    public async Task shared_xaml_document_across_projects_edits_once()
    {
        var workspace = new AdhocWorkspace();
        var libId = ProjectId.CreateNewId();
        var appId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                libId, VersionStamp.Create(), "LibA", "LibA", LanguageNames.CSharp,
                filePath: @"C:\fake-xaml-shared\LibA.csproj"))
            .AddProject(ProjectInfo.Create(
                appId, VersionStamp.Create(), "App", "App", LanguageNames.CSharp,
                filePath: @"C:\fake-xaml-shared\App.csproj"));

        const string libSource = """
            namespace SampleLib
            {
                public class Widget
                {
                    public string Name { get; set; } = "";
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
        const string sharedXaml = """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="clr-namespace:SampleLib"
                    x:Class="AppNs.Uses"
                    x:DataType="local:Widget">
                <TextBlock Text="{Binding Name}" />
            </Window>
            """;

        var runtime = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        solution = solution
            .AddDocument(DocumentId.CreateNewId(libId), "Widget.cs", SourceText.From(libSource),
                filePath: @"C:\fake-xaml-shared\Widget.cs")
            .AddDocument(DocumentId.CreateNewId(appId), "Uses.cs", SourceText.From(appSource),
                filePath: @"C:\fake-xaml-shared\Uses.cs")
            .AddMetadataReference(libId, runtime)
            .AddMetadataReference(appId, runtime)
            .WithProjectCompilationOptions(libId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithProjectCompilationOptions(appId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddProjectReference(appId, new ProjectReference(libId));

        // The same linked file is registered as an AdditionalDocument in both
        // projects, so each project's index emits the same usage point.
        const string sharedPath = @"C:\fake-xaml-shared\Shared.axaml";
        solution = solution.AddAdditionalDocument(
            DocumentId.CreateNewId(appId), "Shared.axaml", SourceText.From(sharedXaml),
            filePath: sharedPath);
        solution = solution.AddAdditionalDocument(
            DocumentId.CreateNewId(libId), "Shared.axaml", SourceText.From(sharedXaml),
            filePath: sharedPath);

        Assert.True(workspace.TryApplyChanges(solution));
        using (workspace)
        {
            var (loaded, session) = WiredSession(workspace);
            await using var _ = loaded;
            using var __ = session;
            var service = Adapters();

            var handle = await ResolveAsync(service, session, "SampleLib.Widget.Name", libId);
            var (draft, error) = await service.BuildRenamePreviewAsync(session, handle, "Title");
            Assert.Null(error);
            Assert.NotNull(draft);
            var xaml = Assert.Single(draft!.Documents, d =>
                string.Equals(d.Path, sharedPath, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(sharedXaml.Replace("{Binding Name}", "{Binding Title}", StringComparison.Ordinal),
                xaml.NewText, StringComparer.Ordinal);
        }
    }

    // ---------- MCP-level coverage (real disk + trusted roots) ----------

    private const string OnDiskAxaml = """
        <Window xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:local="using:SampleApp"
                xmlns:c="clr-namespace:SampleControls"
                x:Class="SampleApp.MainWindow"
                x:DataType="local:Customer"
                HandwrittenTitle="hi">
            <c:FancyButton Label="{Binding Name}"
                           DataContext="{x:Type local:Address}"
                           Click="OnSave">
                <c:FancyButton.Content>
                    <TextBlock Text="{Binding Home.City}" Tag="{Binding}" />
                </c:FancyButton.Content>
            </c:FancyButton>
            <TextBlock x:Name="TitleText" Text="{Binding Title, ElementName=TitleText}" />
        </Window>
        """;

    [Fact]
    public async Task preview_rename_serializes_unverified_mentions_and_omits_when_empty()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "MainWindow.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(axaml, OnDiskAxaml);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithAvaloniaRenameOnDisk(projectDir));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var handle = await ResolveMcpAsync(fx, "SampleApp.Vm.Title");
            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?> { ["handle"] = handle, ["newName"] = "Heading" });
            Assert.True(preview.IsError is not true, InProcessMcpFixture.TextOf(preview));
            var body = InProcessMcpFixture.Deserialize<SymbolPreviewRenameResultDto>(preview);

            var mention = Assert.Single(body.UnverifiedXamlMentions!);
            Assert.Equal(PathCanonical.Of(axaml), mention.Path);
            Assert.Equal("Title", mention.Text);
            var axamlText = SourceText.From(OnDiskAxaml);
            var start = axamlText.Lines.GetPosition(
                new LinePosition(mention.Line - 1, mention.Column - 1));
            var end = axamlText.Lines.GetPosition(
                new LinePosition(mention.EndLine - 1, mention.EndColumn - 1));
            Assert.Equal("Title", OnDiskAxaml[start..end]);
            Assert.DoesNotContain(body.Documents, d =>
                d.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase));

            // camelCase wire shape.
            using var doc = JsonDocument.Parse(InProcessMcpFixture.TextOf(preview));
            var mentions = doc.RootElement.GetProperty("unverifiedXamlMentions").EnumerateArray().ToArray();
            Assert.Single(mentions);
            Assert.Equal("Title", mentions[0].GetProperty("text").GetString());
            Assert.True(mentions[0].TryGetProperty("endColumn", out _));

            // No mentions → the field is omitted entirely.
            var nameHandle = await ResolveMcpAsync(fx, "SampleApp.Customer.Name");
            var cleanPreview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?> { ["handle"] = nameHandle, ["newName"] = "Nickname" });
            Assert.True(cleanPreview.IsError is not true, InProcessMcpFixture.TextOf(cleanPreview));
            using var cleanDoc = JsonDocument.Parse(InProcessMcpFixture.TextOf(cleanPreview));
            Assert.False(cleanDoc.RootElement.TryGetProperty("unverifiedXamlMentions", out _));
            var cleanBody = InProcessMcpFixture.Deserialize<SymbolPreviewRenameResultDto>(cleanPreview);
            var cleanXaml = Assert.Single(cleanBody.Documents, d =>
                d.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("{Binding Nickname}", cleanXaml.NewText, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task xname_generated_field_rename_updates_xaml_and_handwritten_refs()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "MainWindow.axaml");
        var codeBehind = Path.Combine(projectDir, "MainWindow.axaml.cs");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(axaml, OnDiskAxaml);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithAvaloniaRenameOnDisk(projectDir));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            // The generated x:Name field is reached through xaml_resolve_name.
            var resolved = await fx.Client.CallToolAsync(
                "xaml_resolve_name",
                new Dictionary<string, object?> { ["path"] = axaml, ["name"] = "TitleText" });
            Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
            var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;

            var epochBefore = fx.WorkspaceHost.CurrentEpoch;
            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?> { ["handle"] = handle, ["newName"] = "HeaderText" });
            Assert.True(preview.IsError is not true, InProcessMcpFixture.TextOf(preview));
            var body = InProcessMcpFixture.Deserialize<SymbolPreviewRenameResultDto>(preview);

            var xamlSlice = Assert.Single(body.Documents, d =>
                d.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("x:Name=\"HeaderText\"", xamlSlice.NewText, StringComparison.Ordinal);
            var csSlice = Assert.Single(body.Documents, d =>
                d.Path.EndsWith("MainWindow.axaml.cs", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("=> HeaderText", csSlice.NewText, StringComparison.Ordinal);
            // The generated declaration is never sliced — it regenerates.
            Assert.All(body.Documents, d =>
                Assert.DoesNotContain(".g.cs", d.Path, StringComparison.OrdinalIgnoreCase));

            var apply = await fx.Client.CallToolAsync(
                "symbol_apply_rename",
                new Dictionary<string, object?> { ["previewId"] = body.PreviewId });
            Assert.True(apply.IsError is not true, InProcessMcpFixture.TextOf(apply));
            var applied = InProcessMcpFixture.Deserialize<SymbolApplyRenameResultDto>(apply);
            Assert.Equal(fx.WorkspaceHost.CurrentEpoch, applied.Epoch);
            Assert.True(applied.Epoch > epochBefore);
            Assert.Contains(applied.WrittenPaths, p =>
                p.EndsWith("MainWindow.axaml", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(applied.WrittenPaths, p =>
                p.EndsWith("MainWindow.axaml.cs", StringComparison.OrdinalIgnoreCase));
            Assert.All(applied.WrittenPaths, p =>
                Assert.DoesNotContain(".g.cs", p, StringComparison.OrdinalIgnoreCase));

            var xamlAfter = await File.ReadAllTextAsync(axaml);
            Assert.Contains("x:Name=\"HeaderText\"", xamlAfter, StringComparison.Ordinal);
            var csAfter = await File.ReadAllTextAsync(codeBehind);
            Assert.Contains("=> HeaderText", csAfter, StringComparison.Ordinal);
            Assert.DoesNotContain("=> TitleText", csAfter, StringComparison.Ordinal);
            // NOTE: the fake NameGenerator emits a fixed `TitleText` field
            // regardless of x:Name (it does not parse XAML), so after apply the
            // new name does not resolve through this fixture — a real name
            // generator re-emits the field from the updated x:Name value. What
            // this test does prove is the contract: the XAML value and every
            // handwritten reference moved, and no generated document was written.
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task generated_field_without_xname_usage_stays_refused()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "MainWindow.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        // No x:Name at all: the generated field is not reachable through XAML.
        await File.WriteAllTextAsync(axaml, """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="using:SampleApp"
                    x:Class="SampleApp.MainWindow">
            </Window>
            """);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithAvaloniaRenameOnDisk(projectDir));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var handle = await ResolveMcpAsync(fx, "SampleApp.MainWindow.TitleText");
            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?> { ["handle"] = handle, ["newName"] = "HeaderText" });
            Assert.True(preview.IsError is true);
            Assert.Equal(
                PolicyErrorCodes.GeneratedSymbolRenameRefused,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(preview).Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task apply_rename_preserves_bom_advances_epoch_and_adds_no_xaml_diagnostics()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "MainWindow.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        // UTF-8 with BOM — the writer must keep the preamble.
        await File.WriteAllTextAsync(axaml, OnDiskAxaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithAvaloniaRenameOnDisk(projectDir));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var diagnosticsBefore = await XamlDiagnosticsAsync(fx, axaml);
            var handle = await ResolveMcpAsync(fx, "SampleApp.Customer");
            var epochBefore = fx.WorkspaceHost.CurrentEpoch;

            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?> { ["handle"] = handle, ["newName"] = "Client" });
            Assert.True(preview.IsError is not true, InProcessMcpFixture.TextOf(preview));
            var body = InProcessMcpFixture.Deserialize<SymbolPreviewRenameResultDto>(preview);
            Assert.Equal(epochBefore, body.Epoch);
            Assert.Contains(body.Documents, d =>
                d.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase));

            var apply = await fx.Client.CallToolAsync(
                "symbol_apply_rename",
                new Dictionary<string, object?> { ["previewId"] = body.PreviewId });
            Assert.True(apply.IsError is not true, InProcessMcpFixture.TextOf(apply));
            var applied = InProcessMcpFixture.Deserialize<SymbolApplyRenameResultDto>(apply);
            Assert.Equal(fx.WorkspaceHost.CurrentEpoch, applied.Epoch);
            Assert.True(applied.Epoch > epochBefore);
            Assert.Contains(applied.WrittenPaths, p =>
                p.EndsWith("MainWindow.axaml", StringComparison.OrdinalIgnoreCase));

            var bytes = await File.ReadAllBytesAsync(axaml);
            Assert.True(bytes.Length >= 3 &&
                        bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "expected the UTF-8 BOM to be preserved");
            var after = Encoding.UTF8.GetString(bytes);
            Assert.Contains("x:DataType=\"local:Client\"", after, StringComparison.Ordinal);
            Assert.Contains("Tag=\"{Binding}\"", after, StringComparison.Ordinal);

            var diagnosticsAfter = await XamlDiagnosticsAsync(fx, axaml);
            Assert.Equal(
                diagnosticsBefore.Items.Select(static d => d.Id).OrderBy(static id => id),
                diagnosticsAfter.Items.Select(static d => d.Id).OrderBy(static id => id));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task apply_rename_refuses_xaml_with_unreadable_encoding()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "MainWindow.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        // Valid XML carrying bytes that strict UTF-8 refuses: File.ReadAllText
        // decodes leniently so the document registers (the Tag attribute value
        // is never a usage span), but the writer's lossless check fails closed
        // with SourceEncodingRefused.
        var brokenAxaml = OnDiskAxaml.Replace(
            "x:DataType=\"local:Customer\"",
            "x:DataType=\"local:Customer\" Tag=\"BAD\"",
            StringComparison.Ordinal);
        var badAt = brokenAxaml.IndexOf("BAD", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(brokenAxaml[..badAt])
            .Concat(new byte[] { 0xC0, 0xAF }) // overlong sequence — invalid UTF-8
            .Concat(Encoding.UTF8.GetBytes(brokenAxaml[(badAt + 3)..]))
            .ToArray();
        await File.WriteAllBytesAsync(axaml, bytes);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithAvaloniaRenameOnDisk(projectDir));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var handle = await ResolveMcpAsync(fx, "SampleApp.Customer");
            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?> { ["handle"] = handle, ["newName"] = "Client" });
            Assert.True(preview.IsError is not true, InProcessMcpFixture.TextOf(preview));
            var body = InProcessMcpFixture.Deserialize<SymbolPreviewRenameResultDto>(preview);
            Assert.Contains(body.Documents, d =>
                d.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase));

            var apply = await fx.Client.CallToolAsync(
                "symbol_apply_rename",
                new Dictionary<string, object?> { ["previewId"] = body.PreviewId });
            Assert.True(apply.IsError is true);
            Assert.Equal(
                PolicyErrorCodes.SourceEncodingRefused,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(apply).Error);

            // Nothing was written — the refused file and the sources are intact.
            var onDisk = await File.ReadAllBytesAsync(axaml);
            Assert.Equal(bytes, onDisk);
            var csBytes = await File.ReadAllTextAsync(Path.Combine(projectDir, "MainWindow.axaml.cs"));
            Assert.Contains("public class Customer", csBytes, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task diagnostic_fix_and_refactoring_previews_never_slice_xaml()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "MainWindow.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(axaml, OnDiskAxaml);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithMissingUsingOnDisk(projectDir));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var occurrence = await DiagnosticFixSeamTests.FirstCs0246Async(fx);
            var fixPreview = await DiagnosticFixSeamTests.PreviewWorkingFixAsync(fx, occurrence);
            Assert.All(fixPreview.Documents, d =>
                Assert.False(
                    d.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) ||
                    d.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase),
                    $"fix preview sliced XAML document {d.Path}"));
        }
        finally
        {
            TryDelete(root);
        }

        root = CreateTempDir("root");
        projectDir = Path.Combine(root, "lib");
        solution = Path.Combine(root, "App.slnx");
        axaml = Path.Combine(root, "MainWindow.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(axaml, OnDiskAxaml);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithEncapsulateFieldOnDisk(projectDir));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var handle = await CodeRefactoringSeamTests.ResolveAsync(fx, "RefactorApp.Widget.count");
            var refactoringPreview = await CodeRefactoringSeamTests.PreviewWorkingAsync(fx, handle);
            Assert.All(refactoringPreview.Documents, d =>
                Assert.False(
                    d.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) ||
                    d.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase),
                    $"refactoring preview sliced XAML document {d.Path}"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<string> ResolveMcpAsync(InProcessMcpFixture fx, string name)
    {
        var resolved = await fx.Client.CallToolAsync(
            "symbol_resolve",
            new Dictionary<string, object?> { ["name"] = name });
        Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
        return InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;
    }

    private static async Task<ProjectDiagnosticsResultDto> XamlDiagnosticsAsync(
        InProcessMcpFixture fx, string path)
    {
        var result = await fx.Client.CallToolAsync(
            "xaml_diagnostics",
            new Dictionary<string, object?> { ["path"] = path, ["limit"] = 100 });
        Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
        return InProcessMcpFixture.Deserialize<ProjectDiagnosticsResultDto>(result);
    }

    // ---------- workspace builders ----------

    private static AdhocWorkspace AvaloniaRenameWorkspace()
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

                    public event EventHandler Click;
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
            filePath: @"C:\fake-xaml-rename\AvaloniaApp.csproj"));
        solution = solution.AddDocument(
            csId, "MainWindow.axaml.cs", SourceText.From(source),
            filePath: @"C:\fake-xaml-rename\MainWindow.axaml.cs");
        solution = solution.AddAdditionalDocument(
            xamlId, "MainWindow.axaml", SourceText.From(AxamlAll), filePath: AxamlPath);
        solution = solution.WithProjectCompilationOptions(
            projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(
            projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply Avalonia rename AdhocWorkspace.");
        }

        return workspace;
    }

    private static AdhocWorkspace MauiRenameWorkspace()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var csId = DocumentId.CreateNewId(projectId);
        var xamlId = DocumentId.CreateNewId(projectId);
        const string source = """
            using System;
            using Microsoft.Maui.Controls;

            [assembly: XmlnsDefinition("http://schemas.microsoft.com/dotnet/2021/maui", "Microsoft.Maui.Controls")]

            namespace Microsoft.Maui.Controls
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

                public class ContentPage
                {
                }

                public class Label
                {
                    public string Text { get; set; } = "";
                }
            }

            namespace MauiPage
            {
                public partial class MainPage
                {
                    public MainPage()
                    {
                    }

                    private object TitleLabel = new();
                }

                public sealed class MainViewModel
                {
                    public string Title { get; set; } = "hello";
                }
            }
            """;
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "MauiApp",
            "MauiApp",
            LanguageNames.CSharp,
            filePath: @"C:\fake-xaml-rename\MauiApp.csproj"));
        solution = solution.AddDocument(
            csId, "MainPage.xaml.cs", SourceText.From(source),
            filePath: @"C:\fake-xaml-rename\MainPage.xaml.cs");
        solution = solution.AddAdditionalDocument(
            xamlId, "MainPage.xaml", SourceText.From(MauiAll), filePath: MauiPath);
        solution = solution.WithProjectCompilationOptions(
            projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(
            projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply MAUI rename AdhocWorkspace.");
        }

        return workspace;
    }

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

    private static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-xaml-ren-" + prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
        }
    }
}
