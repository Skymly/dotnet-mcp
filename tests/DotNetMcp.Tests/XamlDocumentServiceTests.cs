using DotNetMcp.Core;
using DotNetMcp.Xaml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

public class XamlDocumentServiceTests
{
    private const string AxamlPath = @"C:\fake-xaml-unit\MainWindow.axaml";
    private const string MauiPath = @"C:\fake-xaml-unit\MainPage.xaml";
    private const string WpfPath = @"C:\fake-xaml-unit\MainWindow.xaml";

    private static XamlDocumentService Service()
    {
        var roslyn = new RoslynLanguageAdapter(new GeneratorQueryService());
        return new XamlDocumentService(new LanguageAdapters([roslyn]), roslyn);
    }

    [Fact]
    public async Task resolve_class_avalonia_succeeds()
    {
        using var workspace = AvaloniaWorkspace(AvaloniaWindow("SampleApp.MainWindow"));
        using var session = new FakeSession(workspace);
        var (success, xamlError, symbolError) = await Service().ResolveClassAsync(session, AxamlPath);

        Assert.Null(xamlError);
        Assert.Null(symbolError);
        Assert.NotNull(success);
        Assert.True(SymbolHandle.TryParse(success!.Handle, out var parsed, out _));
        Assert.Equal("csharp", parsed!.Language);
        Assert.Equal("MainWindow", success.Summary.DisplayName);
    }

    [Fact]
    public async Task resolve_class_missing_xclass()
    {
        using var workspace = AvaloniaWorkspace("""
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Title="Sample" />
            """);
        using var session = new FakeSession(workspace);
        var (_, xamlError, _) = await Service().ResolveClassAsync(session, AxamlPath);
        Assert.IsType<MissingXamlClassError>(xamlError);
    }

    [Fact]
    public async Task resolve_class_empty_path_is_not_found()
    {
        using var workspace = AvaloniaWorkspace(AvaloniaWindow("SampleApp.MainWindow"));
        using var session = new FakeSession(workspace);
        var (_, xamlError, _) = await Service().ResolveClassAsync(session, "  ");
        Assert.IsType<XamlDocumentNotFoundError>(xamlError);
    }

    [Fact]
    public async Task resolve_class_unknown_path_is_not_found()
    {
        using var workspace = AvaloniaWorkspace(AvaloniaWindow("SampleApp.MainWindow"));
        using var session = new FakeSession(workspace);
        var (_, xamlError, _) = await Service().ResolveClassAsync(session, @"C:\fake-xaml-unit\Missing.axaml");
        Assert.IsType<XamlDocumentNotFoundError>(xamlError);
    }

    [Fact]
    public async Task resolve_class_wpf_xaml_is_unsupported()
    {
        using var workspace = WorkspaceWithXaml(WpfPath, """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    x:Class="SampleApp.MainWindow" />
            """);
        using var session = new FakeSession(workspace);
        var (_, xamlError, _) = await Service().ResolveClassAsync(session, WpfPath);
        Assert.IsType<UnsupportedXamlDocumentError>(xamlError);
    }

    [Fact]
    public async Task resolve_class_maui_succeeds()
    {
        using var workspace = MauiWorkspace();
        using var session = new FakeSession(workspace);
        var (success, xamlError, symbolError) = await Service().ResolveClassAsync(session, MauiPath);
        Assert.Null(xamlError);
        Assert.Null(symbolError);
        Assert.NotNull(success);
        Assert.Equal("MainPage", success!.Summary.DisplayName);
    }

    [Fact]
    public async Task resolve_name_succeeds()
    {
        using var workspace = AvaloniaWorkspace(AvaloniaWindowWithName());
        using var session = new FakeSession(workspace);
        var (success, xamlError, symbolError) = await Service().ResolveNameAsync(session, AxamlPath, "TitleText");
        Assert.Null(xamlError);
        Assert.Null(symbolError);
        Assert.NotNull(success);
        Assert.Equal("TitleText", success!.Summary.DisplayName);
    }

    [Fact]
    public async Task resolve_name_missing_is_missing_name()
    {
        using var workspace = AvaloniaWorkspace(AvaloniaWindowWithName());
        using var session = new FakeSession(workspace);
        var (_, xamlError, _) = await Service().ResolveNameAsync(session, AxamlPath, "NoSuchName");
        Assert.IsType<MissingXamlNameError>(xamlError);
    }

    [Fact]
    public async Task resolve_name_without_field_is_generator_not_run()
    {
        using var workspace = AvaloniaWorkspace(AvaloniaWindowWithName(), includeNameField: false);
        using var session = new FakeSession(workspace);
        var (_, xamlError, _) = await Service().ResolveNameAsync(session, AxamlPath, "TitleText");
        Assert.IsType<NameGeneratorNotRunError>(xamlError);
    }

    [Fact]
    public async Task resolve_binding_walks_path()
    {
        using var workspace = AvaloniaWorkspace(AvaloniaWindowWithBinding());
        using var session = new FakeSession(workspace);
        var (segments, xamlError, symbolError) = await Service().ResolveBindingAsync(session, AxamlPath, "Home.City");
        Assert.Null(xamlError);
        Assert.Null(symbolError);
        Assert.NotNull(segments);
        Assert.Equal(2, segments!.Count);
        Assert.Equal("Home", segments[0].Name);
        Assert.Equal("City", segments[1].Name);
        Assert.True(SymbolHandle.TryParse(segments[0].Handle, out _, out _));
    }

    [Fact]
    public async Task resolve_binding_missing_datatype()
    {
        using var workspace = AvaloniaWorkspace(AvaloniaWindow("SampleApp.MainWindow"));
        using var session = new FakeSession(workspace);
        var (_, xamlError, _) = await Service().ResolveBindingAsync(session, AxamlPath, "Home.City");
        Assert.IsType<MissingDataTypeError>(xamlError);
    }

    [Fact]
    public async Task resolve_binding_unknown_property()
    {
        using var workspace = AvaloniaWorkspace(AvaloniaWindowWithBinding());
        using var session = new FakeSession(workspace);
        var (_, xamlError, _) = await Service().ResolveBindingAsync(session, AxamlPath, "Nope");
        Assert.IsType<BindingPropertyNotFoundError>(xamlError);
    }

    [Fact]
    public async Task resolve_binding_method_is_type_mismatch()
    {
        using var workspace = AvaloniaWorkspace(AvaloniaWindowWithBinding());
        using var session = new FakeSession(workspace);
        var (_, xamlError, _) = await Service().ResolveBindingAsync(session, AxamlPath, "Save");
        Assert.IsType<BindingTypeMismatchError>(xamlError);
    }

    [Fact]
    public async Task list_xmlns_returns_mappings()
    {
        using var workspace = AvaloniaWorkspace(AvaloniaWindowWithBinding());
        using var session = new FakeSession(workspace);
        var (items, error) = await Service().ListXmlnsAsync(session, AxamlPath);
        Assert.Null(error);
        Assert.NotNull(items);
        Assert.Contains(items!, i => i.Source == XamlXmlnsSource.Using);
        Assert.Contains(items, i => i.Prefix == "x");
    }

    [Fact]
    public async Task list_xmlns_unknown_prefix()
    {
        using var workspace = AvaloniaWorkspace(AvaloniaWindowWithBinding());
        using var session = new FakeSession(workspace);
        var (_, error) = await Service().ListXmlnsAsync(session, AxamlPath, prefix: "nope");
        Assert.IsType<UnknownXmlnsPrefixError>(error);
    }

    [Fact]
    public async Task get_diagnostics_returns_a_page()
    {
        using var workspace = AvaloniaWorkspace(AvaloniaWindowWithBinding());
        using var session = new FakeSession(workspace);
        var (page, xamlError, symbolError) = await Service().GetDiagnosticsAsync(session, AxamlPath);
        Assert.Null(xamlError);
        Assert.Null(symbolError);
        Assert.NotNull(page);
    }

    private static AdhocWorkspace AvaloniaWorkspace(string axaml, bool includeNameField = true)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var csId = DocumentId.CreateNewId(projectId);
        var xamlId = DocumentId.CreateNewId(projectId);
        var field = includeNameField
            ? "        private object TitleText = new();\n"
            : "";
        var source = $$"""
            namespace SampleApp;

            public partial class MainWindow
            {
                public MainWindow()
                {
                }
            {{field}}        public string HandwrittenTitle { get; set; } = "";
            }

            public class Address
            {
                public string City { get; set; } = "";
            }

            public class Customer
            {
                public Address Home { get; set; } = new();
                public string Name { get; set; } = "";
                public void Save() { }
            }
            """;

        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "AvaloniaApp",
            "AvaloniaApp",
            LanguageNames.CSharp,
            filePath: @"C:\fake-xaml-unit\AvaloniaApp.csproj"));
        solution = solution.AddDocument(csId, "MainWindow.axaml.cs", SourceText.From(source), filePath: @"C:\fake-xaml-unit\MainWindow.axaml.cs");
        solution = solution.AddAdditionalDocument(xamlId, "MainWindow.axaml", SourceText.From(axaml), filePath: AxamlPath);
        solution = solution.WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply Avalonia AdhocWorkspace.");
        }

        return workspace;
    }

    private static AdhocWorkspace MauiWorkspace()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var csId = DocumentId.CreateNewId(projectId);
        var xamlId = DocumentId.CreateNewId(projectId);
        const string source = """
            namespace MauiPage;

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
            """;
        const string xaml = """
            <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:local="clr-namespace:MauiPage"
                         x:Class="MauiPage.MainPage"
                         x:DataType="local:MainViewModel">
                <Label x:Name="TitleLabel" Text="{Binding Title}" />
            </ContentPage>
            """;
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "MauiApp",
            "MauiApp",
            LanguageNames.CSharp,
            filePath: @"C:\fake-xaml-unit\MauiApp.csproj"));
        solution = solution.AddDocument(csId, "MainPage.xaml.cs", SourceText.From(source), filePath: @"C:\fake-xaml-unit\MainPage.xaml.cs");
        solution = solution.AddAdditionalDocument(xamlId, "MainPage.xaml", SourceText.From(xaml), filePath: MauiPath);
        solution = solution.WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply MAUI AdhocWorkspace.");
        }

        return workspace;
    }

    private static AdhocWorkspace WorkspaceWithXaml(string path, string xaml)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var xamlId = DocumentId.CreateNewId(projectId);
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "Lib",
            "Lib",
            LanguageNames.CSharp,
            filePath: @"C:\fake-xaml-unit\Lib.csproj"));
        solution = solution.AddAdditionalDocument(xamlId, Path.GetFileName(path), SourceText.From(xaml), filePath: path);
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply XAML AdhocWorkspace.");
        }

        return workspace;
    }

    private static string AvaloniaWindow(string className) =>
        $"""
        <Window xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                x:Class="{className}"
                Title="Sample" />
        """;

    private static string AvaloniaWindowWithName() =>
        """
        <Window xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                x:Class="SampleApp.MainWindow">
            <TextBlock x:Name="TitleText" Text="Hi" />
        </Window>
        """;

    private static string AvaloniaWindowWithBinding() =>
        """
        <Window xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:local="using:SampleApp"
                x:Class="SampleApp.MainWindow"
                x:DataType="local:Customer">
            <TextBlock Text="{Binding Home.City}" />
        </Window>
        """;

    private sealed class FakeSession : IWorkspaceSession
    {
        private readonly AdhocWorkspace _workspace;

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

        private readonly GeneratorRunCache _cache = new();

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