using DotNetMcp.Core;
using DotNetMcp.Server;
using DotNetMcp.Xaml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetMcp.Tests;

public class XamlUsageIndexTests
{
    private const string AxamlPath = @"C:\fake-xaml-usage\MainWindow.axaml";
    private const string MauiPath = @"C:\fake-xaml-usage\MainPage.xaml";
    private const string WpfPath = @"C:\fake-xaml-usage\MainWindow.xaml";

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

    private const string MauiAll = """
        <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:local="clr-namespace:MauiPage"
                     x:Class="MauiPage.MainPage"
                     x:DataType="local:MainViewModel">
            <Label x:Name="TitleLabel" Text="{Binding Title}" />
        </ContentPage>
        """;

    private static XamlUsageIndex Index()
    {
        var roslyn = new RoslynLanguageAdapter(new GeneratorQueryService());
        var languages = new LanguageAdapters([roslyn]);
        return new XamlUsageIndex(new XamlDocumentService(languages, roslyn), languages, roslyn);
    }

    private static string SingleProjectId(AdhocWorkspace workspace) =>
        workspace.CurrentSolution.Projects.Single().Id.Id.ToString("D");

    [Fact]
    public async Task avalonia_document_reports_every_usage_kind()
    {
        using var workspace = AvaloniaUsageWorkspace(AxamlAll);
        using var session = new FakeSession(workspace);
        var text = SourceText.From(AxamlAll);
        var projectId = SingleProjectId(workspace);

        var usages = await Index().GetUsagesAsync(session, projectId);

        Assert.NotEmpty(usages);
        Assert.All(usages, u => AssertQuad(u, text));
        Assert.All(usages, u =>
        {
            Assert.Equal(projectId, u.ProjectId);
            Assert.True(SymbolHandle.TryParse(u.Handle, out var parsed, out _), u.Handle);
            Assert.Equal("csharp", parsed!.Language);
            Assert.Equal(u.SymbolKey, parsed.SignatureQualifiedName);
            Assert.Equal(u.Snippet, u.Snippet.Trim());
            Assert.True(u.Snippet.Length <= 80, u.Snippet);
        });

        var xclass = Assert.Single(usages, u => u.Kind == XamlUsageKind.XClass);
        AssertUsage(xclass, AxamlAll, "SampleApp.MainWindow", "SampleApp.MainWindow");

        var windows = usages.Where(u => u.Kind == XamlUsageKind.ElementType &&
                                        u.SymbolKey == "SampleControls.Window").ToArray();
        Assert.Equal(2, windows.Length); // start + end tag
        AssertUsage(windows[0], AxamlAll, "Window", "SampleControls.Window");

        var fancy = usages.Where(u => u.Kind == XamlUsageKind.ElementType &&
                                      u.SymbolKey == "SampleControls.FancyButton").ToArray();
        Assert.Equal(4, fancy.Length); // element start/end + property-element owner start/end
        AssertUsage(fancy[0], AxamlAll, "c:FancyButton", "SampleControls.FancyButton");

        var textBlocks = usages.Where(u => u.Kind == XamlUsageKind.ElementType &&
                                           u.SymbolKey == "SampleControls.TextBlock").ToArray();
        Assert.Equal(2, textBlocks.Length); // two self-closing elements
        AssertUsage(textBlocks[0], AxamlAll, "TextBlock", "SampleControls.TextBlock");

        var title = Assert.Single(usages, u => u.SymbolKey == "SampleControls.Window.HandwrittenTitle");
        AssertUsage(title, AxamlAll, "HandwrittenTitle", "SampleControls.Window.HandwrittenTitle");

        var label = Assert.Single(usages, u => u.SymbolKey == "SampleControls.FancyButton.Label");
        AssertUsage(label, AxamlAll, "Label", "SampleControls.FancyButton.Label");

        var click = Assert.Single(usages, u => u.SymbolKey == "SampleControls.FancyButton.Click");
        AssertUsage(click, AxamlAll, "Click", "SampleControls.FancyButton.Click");

        var dataType = Assert.Single(usages, u => u.Kind == XamlUsageKind.XDataType);
        AssertUsage(dataType, AxamlAll, "local:Customer", "SampleApp.Customer");

        var markupType = Assert.Single(usages, u => u.Kind == XamlUsageKind.MarkupTypeRef);
        AssertUsage(markupType, AxamlAll, "local:Address", "SampleApp.Address");

        var name = Assert.Single(usages, u => u.Kind == XamlUsageKind.XName);
        AssertUsage(name, AxamlAll, "SaveButton", "SampleApp.MainWindow.SaveButton");

        var handler = Assert.Single(usages, u => u.Kind == XamlUsageKind.EventHandler);
        AssertUsage(handler, AxamlAll, "OnSave", "SampleApp.MainWindow.OnSave()");

        var nameSegment = Assert.Single(usages, u => u.SymbolKey == "SampleApp.Customer.Name");
        AssertUsage(nameSegment, AxamlAll, "Name", "SampleApp.Customer.Name", expectExact: true);

        var homeSegment = Assert.Single(usages, u => u.SymbolKey == "SampleApp.Customer.Home");
        AssertUsage(homeSegment, AxamlAll, "Home", "SampleApp.Customer.Home", expectExact: true);

        var citySegment = Assert.Single(usages, u => u.SymbolKey == "SampleApp.Address.City");
        AssertUsage(citySegment, AxamlAll, "City", "SampleApp.Address.City", expectExact: true);

        var selfSegment = Assert.Single(usages, u =>
            u.Kind == XamlUsageKind.BindingSegment && u.SymbolKey == "SampleApp.Customer");
        AssertUsage(selfSegment, AxamlAll, "{Binding}", "SampleApp.Customer", expectExact: true);

        var unverified = Assert.Single(usages, u => u.SymbolKey == "SampleApp.Vm.Title");
        Assert.Equal(XamlUsageKind.BindingSegment, unverified.Kind);
        Assert.Equal(XamlUsageConfidence.Unverified, unverified.Confidence);
        AssertUsage(unverified, AxamlAll, "Title", "SampleApp.Vm.Title", expectExact: false);
    }

    [Fact]
    public async Task maui_document_reports_class_element_binding_and_name()
    {
        using var workspace = MauiUsageWorkspace();
        using var session = new FakeSession(workspace);
        var text = SourceText.From(MauiAll);
        var projectId = SingleProjectId(workspace);

        var usages = await Index().GetUsagesAsync(session, projectId);

        Assert.NotEmpty(usages);
        Assert.All(usages, u => AssertQuad(u, text));

        var xclass = Assert.Single(usages, u => u.Kind == XamlUsageKind.XClass);
        Assert.Equal("MauiPage.MainPage", xclass.SymbolKey);

        Assert.Contains(usages, u =>
            u.Kind == XamlUsageKind.ElementType && u.SymbolKey == "Microsoft.Maui.Controls.ContentPage");
        Assert.Contains(usages, u =>
            u.Kind == XamlUsageKind.ElementType && u.SymbolKey == "Microsoft.Maui.Controls.Label");

        var xname = Assert.Single(usages, u => u.Kind == XamlUsageKind.XName);
        Assert.Equal("MauiPage.MainPage.TitleLabel", xname.SymbolKey);

        var dataType = Assert.Single(usages, u => u.Kind == XamlUsageKind.XDataType);
        Assert.Equal("MauiPage.MainViewModel", dataType.SymbolKey);

        var property = Assert.Single(usages, u => u.Kind == XamlUsageKind.PropertyAttribute);
        Assert.Equal("Microsoft.Maui.Controls.Label.Text", property.SymbolKey);

        var segment = Assert.Single(usages, u => u.Kind == XamlUsageKind.BindingSegment);
        Assert.Equal("MauiPage.MainViewModel.Title", segment.SymbolKey);
        Assert.Equal(XamlUsageConfidence.Exact, segment.Confidence);
    }

    [Fact]
    public async Task wpf_xaml_is_not_a_registered_document_and_indexes_nothing()
    {
        using var workspace = WpfUsageWorkspace();
        using var session = new FakeSession(workspace);

        var usages = await Index().GetUsagesAsync(session, SingleProjectId(workspace));

        Assert.Empty(usages);
    }

    [Fact]
    public void is_xaml_document_path_follows_registered_extensions()
    {
        var index = Index();
        Assert.True(index.IsXamlDocumentPath(AxamlPath));
        Assert.True(index.IsXamlDocumentPath(MauiPath));
        Assert.True(index.IsXamlDocumentPath(@"C:\fake\VIEW.AXAML"));
        Assert.False(index.IsXamlDocumentPath(@"C:\fake\MainWindow.axaml.cs"));
        Assert.False(index.IsXamlDocumentPath(@"C:\fake\page.html"));
        Assert.False(index.IsXamlDocumentPath("   "));
    }

    [Fact]
    public async Task find_at_position_returns_the_covering_usage()
    {
        using var workspace = AvaloniaUsageWorkspace(AxamlAll);
        using var session = new FakeSession(workspace);
        var projectId = SingleProjectId(workspace);
        var text = SourceText.From(AxamlAll);
        var index = Index();

        var atClass = await FindAtAsync(index, session, text, AxamlAll.IndexOf("SampleApp.MainWindow", StringComparison.Ordinal) + 3, projectId);
        Assert.Equal(XamlUsageKind.XClass, atClass!.Kind);

        var atHandler = await FindAtAsync(index, session, text, AxamlAll.IndexOf("OnSave", StringComparison.Ordinal) + 1, projectId);
        Assert.Equal(XamlUsageKind.EventHandler, atHandler!.Kind);
        Assert.Equal("SampleApp.MainWindow.OnSave()", atHandler.SymbolKey);

        var atCity = await FindAtAsync(index, session, text, AxamlAll.IndexOf("City}", StringComparison.Ordinal) + 1, projectId);
        Assert.Equal(XamlUsageKind.BindingSegment, atCity!.Kind);
        Assert.Equal("SampleApp.Address.City", atCity.SymbolKey);

        var atElement = await FindAtAsync(index, session, text, AxamlAll.IndexOf("TextBlock", StringComparison.Ordinal) + 2, projectId);
        Assert.Equal(XamlUsageKind.ElementType, atElement!.Kind);
        Assert.Equal("SampleControls.TextBlock", atElement.SymbolKey);
    }

    [Fact]
    public async Task find_at_position_returns_null_outside_usage_spans()
    {
        using var workspace = AvaloniaUsageWorkspace(AxamlAll);
        using var session = new FakeSession(workspace);
        var projectId = SingleProjectId(workspace);
        var text = SourceText.From(AxamlAll);
        var index = Index();

        // Inside the HandwrittenTitle attribute VALUE — not a usage.
        var gap = await FindAtAsync(index, session, text, AxamlAll.IndexOf("\"hi\"", StringComparison.Ordinal) + 2, projectId);
        Assert.Null(gap);

        // EndColumn is exclusive: the character right after the x:Class token misses.
        var xclass = Assert.Single(
            await index.GetUsagesAsync(session, projectId),
            u => u.Kind == XamlUsageKind.XClass);
        Assert.Null(await index.FindAtPositionAsync(session, AxamlPath, xclass.EndLine, xclass.EndColumn, projectId));

        Assert.Null(await index.FindAtPositionAsync(session, AxamlPath, 0, 1, projectId));
        Assert.Null(await index.FindAtPositionAsync(session, @"C:\fake-xaml-usage\MainWindow.cs", 1, 1, projectId));
        Assert.Null(await index.FindAtPositionAsync(session, AxamlPath, 1, 1, projectId: "does-not-exist"));
    }

    [Fact]
    public async Task find_at_position_searches_all_projects_when_project_id_is_omitted()
    {
        using var workspace = AvaloniaUsageWorkspace(AxamlAll);
        using var session = new FakeSession(workspace);
        var text = SourceText.From(AxamlAll);
        var index = Index();

        var found = await FindAtAsync(index, session, text, AxamlAll.IndexOf("OnSave", StringComparison.Ordinal) + 1, projectId: null);

        Assert.NotNull(found);
        Assert.Equal(XamlUsageKind.EventHandler, found!.Kind);
    }

    [Fact]
    public async Task session_without_caches_still_builds_the_index()
    {
        using var workspace = AvaloniaUsageWorkspace(AxamlAll);
        var session = new FakeSession(workspace);
        Assert.False((object)session is IWorkspaceSessionCaches);

        var index = Index();
        var projectId = SingleProjectId(workspace);
        var first = await index.GetUsagesAsync(session, projectId);
        var second = await index.GetUsagesAsync(session, projectId);

        Assert.NotEmpty(first);
        Assert.NotSame(first, second);
        Assert.Equal(first.Select(u => (u.Start, u.Kind, u.SymbolKey)),
                     second.Select(u => (u.Start, u.Kind, u.SymbolKey)));
    }

    [Fact]
    public async Task usage_cache_is_keyed_per_epoch_and_project()
    {
        using var workspace = AvaloniaUsageWorkspace(AxamlAll);
        await using var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        var cache = new XamlUsageIndexCache();
        var index = Index();
        var projectId = SingleProjectId(workspace);

        using var first = new WorkspaceSession(loaded, epoch: 1, xamlUsageCache: cache);
        var a = await index.GetUsagesAsync(first, projectId);
        var b = await index.GetUsagesAsync(first, projectId);
        Assert.Same(a, b);
        Assert.True(cache.TryGet(1, projectId, out _));

        using var second = new WorkspaceSession(loaded, epoch: 2, xamlUsageCache: cache);
        var c = await index.GetUsagesAsync(second, projectId);
        Assert.NotSame(a, c);
        Assert.False(cache.TryGet(1, projectId, out _));
        Assert.True(cache.TryGet(2, projectId, out var cached));
        Assert.Same(c, cached);
    }

    [Fact]
    public void core_session_contract_does_not_expose_the_usage_cache()
    {
        Assert.DoesNotContain(
            typeof(IWorkspaceSession).GetProperties(),
            p => p.Name == nameof(IWorkspaceSessionCaches.XamlUsages));
    }

    [Fact]
    public void di_wires_xaml_usage_source_to_the_index_singleton()
    {
        var root = Path.Combine(Path.GetTempPath(), "dotnet-mcp-xaml-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var services = new ServiceCollection();
            ServerHost.AddDotNetMcp(services, TrustedRoots.Create([root]));
            using var provider = services.BuildServiceProvider();

            var source = provider.GetRequiredService<IXamlUsageSource>();
            Assert.IsType<XamlUsageIndex>(source);
            Assert.Same(source, provider.GetRequiredService<IXamlUsageSource>());
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task<XamlUsagePoint?> FindAtAsync(
        XamlUsageIndex index,
        IWorkspaceSession session,
        SourceText text,
        int offset,
        string? projectId)
    {
        var position = text.Lines.GetLinePosition(offset);
        return await index.FindAtPositionAsync(
            session, AxamlPath, position.Line + 1, position.Character + 1, projectId);
    }

    private static void AssertQuad(XamlUsagePoint usage, SourceText text)
    {
        var start = text.Lines.GetPosition(new LinePosition(usage.Line - 1, usage.Column - 1));
        var end = text.Lines.GetPosition(new LinePosition(usage.EndLine - 1, usage.EndColumn - 1));
        Assert.Equal(usage.Start, start);
        Assert.Equal(usage.Start + usage.Length, end);
    }

    private static void AssertUsage(
        XamlUsagePoint usage,
        string xaml,
        string spanText,
        string symbolKey,
        bool expectExact = true)
    {
        Assert.Equal(spanText, xaml.Substring(usage.Start, usage.Length));
        Assert.Equal(symbolKey, usage.SymbolKey);
        Assert.Equal(
            expectExact ? XamlUsageConfidence.Exact : XamlUsageConfidence.Unverified,
            usage.Confidence);
    }

    private static AdhocWorkspace AvaloniaUsageWorkspace(string axaml)
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
            filePath: @"C:\fake-xaml-usage\AvaloniaApp.csproj"));
        solution = solution.AddDocument(
            csId, "MainWindow.axaml.cs", SourceText.From(source),
            filePath: @"C:\fake-xaml-usage\MainWindow.axaml.cs");
        solution = solution.AddAdditionalDocument(
            xamlId, "MainWindow.axaml", SourceText.From(axaml), filePath: AxamlPath);
        solution = solution.WithProjectCompilationOptions(
            projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(
            projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply Avalonia usage AdhocWorkspace.");
        }

        return workspace;
    }

    private static AdhocWorkspace MauiUsageWorkspace()
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
                    public MainPage() { }

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
            filePath: @"C:\fake-xaml-usage\MauiApp.csproj"));
        solution = solution.AddDocument(
            csId, "MainPage.xaml.cs", SourceText.From(source),
            filePath: @"C:\fake-xaml-usage\MainPage.xaml.cs");
        solution = solution.AddAdditionalDocument(
            xamlId, "MainPage.xaml", SourceText.From(MauiAll), filePath: MauiPath);
        solution = solution.WithProjectCompilationOptions(
            projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(
            projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply MAUI usage AdhocWorkspace.");
        }

        return workspace;
    }

    private static AdhocWorkspace WpfUsageWorkspace()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var xamlId = DocumentId.CreateNewId(projectId);
        const string xaml = """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    x:Class="SampleApp.MainWindow">
                <TextBlock Text="{Binding Name}" />
            </Window>
            """;
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "WpfApp",
            "WpfApp",
            LanguageNames.CSharp,
            filePath: @"C:\fake-xaml-usage\WpfApp.csproj"));
        solution = solution.AddAdditionalDocument(
            xamlId, "MainWindow.xaml", SourceText.From(xaml), filePath: WpfPath);
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply WPF usage AdhocWorkspace.");
        }

        return workspace;
    }

    private sealed class FakeSession : IWorkspaceSession
    {
        private readonly AdhocWorkspace _workspace;
        private readonly GeneratorRunCache _cache = new();

        public FakeSession(AdhocWorkspace workspace, long epoch = 1)
        {
            _workspace = workspace;
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

        public void Dispose() => _workspace.Dispose();
    }
}
