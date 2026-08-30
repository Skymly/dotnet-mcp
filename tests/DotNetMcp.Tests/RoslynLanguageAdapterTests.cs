using DotNetMcp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

public class RoslynLanguageAdapterTests
{
    private static RoslynLanguageAdapter Adapter() => new(new GeneratorQueryService());

    [Theory]
    [InlineData("csharp", true)]
    [InlineData("vb", true)]
    [InlineData("fsharp", false)]
    [InlineData("python", false)]
    public void owns_language(string token, bool expected) =>
        Assert.Equal(expected, Adapter().OwnsLanguage(token));

    [Fact]
    public async Task resolve_by_name_unique_csharp_type_succeeds()
    {
        using var workspace = CreateWorkspace(WidgetSource);
        using var session = new FakeSession(workspace.CurrentSolution);
        var (success, error) = await Adapter().ResolveByNameAsync(session, "Widget");

        Assert.Null(error);
        Assert.NotNull(success);
        Assert.True(SymbolHandle.TryParse(success!.Handle, out var parsed, out _));
        Assert.Equal("csharp", parsed!.Language);
        Assert.Equal("Widget", success.Summary.DisplayName);
    }

    [Fact]
    public async Task resolve_by_name_blank_is_not_found()
    {
        using var workspace = CreateWorkspace(WidgetSource);
        using var session = new FakeSession(workspace.CurrentSolution);
        var (_, error) = await Adapter().ResolveByNameAsync(session, "  ");
        Assert.IsType<SymbolNotFoundError>(error);
    }

    [Fact]
    public async Task resolve_by_name_missing_is_not_found()
    {
        using var workspace = CreateWorkspace(WidgetSource);
        using var session = new FakeSession(workspace.CurrentSolution);
        var (_, error) = await Adapter().ResolveByNameAsync(session, "DoesNotExist");
        Assert.IsType<SymbolNotFoundError>(error);
    }

    [Fact]
    public async Task resolve_by_name_unknown_project_is_not_found()
    {
        using var workspace = CreateWorkspace(WidgetSource);
        using var session = new FakeSession(workspace.CurrentSolution);
        var (_, error) = await Adapter().ResolveByNameAsync(session, "Widget", projectId: "missing");
        Assert.IsType<SymbolNotFoundError>(error);
    }

    [Fact]
    public async Task resolve_by_name_two_source_definitions_is_ambiguous()
    {
        using var workspace = CreateWorkspace("""
namespace A { public class Widget {} }
namespace B { public class Widget {} }
""");
        using var session = new FakeSession(workspace.CurrentSolution);
        var (_, error) = await Adapter().ResolveByNameAsync(session, "Widget");
        Assert.IsType<SymbolAmbiguousError>(error);
    }

    [Fact]
    public async Task get_members_returns_first_page_not_truncated()
    {
        using var workspace = CreateWorkspace(WidgetSource);
        using var session = new FakeSession(workspace.CurrentSolution);
        var adapter = Adapter();
        var (resolved, resolveError) = await adapter.ResolveByNameAsync(session, "Widget");
        Assert.Null(resolveError);

        var (page, error) = await adapter.GetMembersAsync(session, resolved!.Handle);
        Assert.Null(error);
        Assert.NotNull(page);
        Assert.Contains(page!.Items, i => i.Summary.DisplayName == "N");
        Assert.Contains(page.Items, i => i.Summary.DisplayName == "M");
        Assert.False(page.Truncated);
    }

    [Fact]
    public async Task get_members_unparseable_handle_is_invalid()
    {
        using var workspace = CreateWorkspace(WidgetSource);
        using var session = new FakeSession(workspace.CurrentSolution);
        var (_, error) = await Adapter().GetMembersAsync(session, "");
        Assert.IsType<InvalidSymbolHandleError>(error);
    }

    [Fact]
    public async Task get_members_fsharp_handle_is_invalid()
    {
        using var workspace = CreateWorkspace(WidgetSource);
        using var session = new FakeSession(workspace.CurrentSolution);
        var handle = SymbolHandle.Create("fsharp", ProjectIdOf(workspace), "FsLib.Widget").Format();
        var (_, error) = await Adapter().GetMembersAsync(session, handle);
        Assert.IsType<InvalidSymbolHandleError>(error);
    }

    [Fact]
    public async Task get_members_unknown_project_is_not_found()
    {
        using var workspace = CreateWorkspace(WidgetSource);
        using var session = new FakeSession(workspace.CurrentSolution);
        var handle = SymbolHandle.Create("csharp", Guid.NewGuid().ToString("D"), "Lib.Widget").Format();
        var (_, error) = await Adapter().GetMembersAsync(session, handle);
        Assert.IsType<SymbolNotFoundError>(error);
    }

    [Fact]
    public async Task get_members_unavailable_compilation_is_not_found()
    {
        using var workspace = CreateWorkspace(WidgetSource);
        using var session = new FakeSession(workspace.CurrentSolution, compilationUnavailable: true);
        var handle = SymbolHandle.Create("csharp", ProjectIdOf(workspace), "Lib.Widget").Format();
        var (_, error) = await Adapter().GetMembersAsync(session, handle);
        Assert.IsType<SymbolNotFoundError>(error);
    }

    [Fact]
    public async Task get_members_missing_signature_is_not_found()
    {
        using var workspace = CreateWorkspace(WidgetSource);
        using var session = new FakeSession(workspace.CurrentSolution);
        var handle = SymbolHandle.Create("csharp", ProjectIdOf(workspace), "Lib.Gone").Format();
        var (_, error) = await Adapter().GetMembersAsync(session, handle);
        Assert.IsType<SymbolNotFoundError>(error);
    }

    [Fact]
    public async Task get_members_non_type_handle_is_not_found()
    {
        using var workspace = CreateWorkspace(WidgetSource);
        using var session = new FakeSession(workspace.CurrentSolution);
        var adapter = Adapter();
        var (resolved, _) = await adapter.ResolveByNameAsync(session, "Widget");
        var (page, _) = await adapter.GetMembersAsync(session, resolved!.Handle);
        var method = Assert.Single(page!.Items, i => i.Summary.DisplayName == "M");

        var (_, error) = await adapter.GetMembersAsync(session, method.Handle);
        Assert.IsType<SymbolNotFoundError>(error);
    }

    [Fact]
    public async Task get_members_bad_cursor_is_stale()
    {
        using var workspace = CreateWorkspace(WidgetSource);
        using var session = new FakeSession(workspace.CurrentSolution);
        var adapter = Adapter();
        var (resolved, _) = await adapter.ResolveByNameAsync(session, "Widget");
        var (_, error) = await adapter.GetMembersAsync(session, resolved!.Handle, cursor: "not-a-cursor");
        Assert.IsType<StaleCursorError>(error);
    }

    [Fact]
    public async Task get_members_wrong_epoch_cursor_is_stale()
    {
        using var workspace = CreateWorkspace(WidgetSource);
        using var session = new FakeSession(workspace.CurrentSolution, epoch: 1);
        var adapter = Adapter();
        var (resolved, _) = await adapter.ResolveByNameAsync(session, "Widget");
        var cursor = MemberPageCursor.Encode(99, 0);
        var (_, error) = await adapter.GetMembersAsync(session, resolved!.Handle, cursor: cursor);
        Assert.IsType<StaleCursorError>(error);
    }

    [Fact]
    public async Task get_members_past_end_cursor_is_stale()
    {
        using var workspace = CreateWorkspace(WidgetSource);
        using var session = new FakeSession(workspace.CurrentSolution, epoch: 1);
        var adapter = Adapter();
        var (resolved, _) = await adapter.ResolveByNameAsync(session, "Widget");
        var cursor = MemberPageCursor.Encode(1, 999);
        var (_, error) = await adapter.GetMembersAsync(session, resolved!.Handle, cursor: cursor);
        Assert.IsType<StaleCursorError>(error);
    }

    [Fact]
    public async Task build_rename_preview_handwritten_succeeds()
    {
        using var workspace = CreateWorkspace(WidgetSource);
        using var session = new FakeSession(workspace.CurrentSolution);
        var adapter = Adapter();
        var (resolved, _) = await adapter.ResolveByNameAsync(session, "Widget");

        var (draft, error) = await adapter.BuildRenamePreviewAsync(session, resolved!.Handle, "Gadget");
        Assert.Null(error);
        Assert.NotNull(draft);
        Assert.Equal("Gadget", draft!.NewName);
        Assert.NotEmpty(draft.Documents);
        Assert.All(draft.Documents, s => Assert.DoesNotContain(".g.cs", Path.GetFileName(s.Path), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(draft.Documents, s => s.NewText.Contains("Gadget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task build_rename_preview_illegal_name_is_invalid()
    {
        using var workspace = CreateWorkspace(WidgetSource);
        using var session = new FakeSession(workspace.CurrentSolution);
        var adapter = Adapter();
        var (resolved, _) = await adapter.ResolveByNameAsync(session, "Widget");
        var (_, error) = await adapter.BuildRenamePreviewAsync(session, resolved!.Handle, "A.B");
        Assert.IsType<InvalidRenameNameError>(error);
    }

    [Fact]
    public async Task build_rename_preview_same_name_is_invalid()
    {
        using var workspace = CreateWorkspace(WidgetSource);
        using var session = new FakeSession(workspace.CurrentSolution);
        var adapter = Adapter();
        var (resolved, _) = await adapter.ResolveByNameAsync(session, "Widget");
        var (_, error) = await adapter.BuildRenamePreviewAsync(session, resolved!.Handle, "Widget");
        Assert.IsType<InvalidRenameNameError>(error);
    }

    [Fact]
    public async Task build_rename_preview_unparseable_handle_is_invalid()
    {
        using var workspace = CreateWorkspace(WidgetSource);
        using var session = new FakeSession(workspace.CurrentSolution);
        var (_, error) = await Adapter().BuildRenamePreviewAsync(session, "", "Gadget");
        Assert.IsType<InvalidSymbolHandleError>(error);
    }

    [Fact]
    public async Task build_rename_preview_fsharp_handle_is_invalid()
    {
        using var workspace = CreateWorkspace(WidgetSource);
        using var session = new FakeSession(workspace.CurrentSolution);
        var handle = SymbolHandle.Create("fsharp", ProjectIdOf(workspace), "FsLib.Widget").Format();
        var (_, error) = await Adapter().BuildRenamePreviewAsync(session, handle, "Gadget");
        Assert.IsType<InvalidSymbolHandleError>(error);
    }

    [Fact]
    public async Task build_rename_preview_generated_origin_is_refused()
    {
        using var workspace = CreateGeneratorWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var adapter = Adapter();
        var (resolved, resolveError) = await adapter.ResolveByNameAsync(session, "CustomMarker");
        Assert.Null(resolveError);

        var (_, error) = await adapter.BuildRenamePreviewAsync(session, resolved!.Handle, "RenamedMarker");
        Assert.IsType<GeneratedSymbolRenameRefusedError>(error);
    }

    [Fact]
    public async Task build_rename_preview_missing_project_is_not_found()
    {
        using var workspace = CreateWorkspace(WidgetSource);
        using var session = new FakeSession(workspace.CurrentSolution);
        var handle = SymbolHandle.Create("csharp", Guid.NewGuid().ToString("D"), "Lib.Widget").Format();
        var (_, error) = await Adapter().BuildRenamePreviewAsync(session, handle, "Gadget");
        Assert.IsType<SymbolNotFoundError>(error);
    }

    [Fact]
    public async Task build_rename_preview_unavailable_compilation_is_not_found()
    {
        using var workspace = CreateWorkspace(WidgetSource);
        using var session = new FakeSession(workspace.CurrentSolution, compilationUnavailable: true);
        var handle = SymbolHandle.Create("csharp", ProjectIdOf(workspace), "Lib.Widget").Format();
        var (_, error) = await Adapter().BuildRenamePreviewAsync(session, handle, "Gadget");
        Assert.IsType<SymbolNotFoundError>(error);
    }

    [Fact]
    public async Task build_rename_preview_missing_signature_is_not_found()
    {
        using var workspace = CreateWorkspace(WidgetSource);
        using var session = new FakeSession(workspace.CurrentSolution);
        var handle = SymbolHandle.Create("csharp", ProjectIdOf(workspace), "Lib.Gone").Format();
        var (_, error) = await Adapter().BuildRenamePreviewAsync(session, handle, "Gadget");
        Assert.IsType<SymbolNotFoundError>(error);
    }

    private const string WidgetSource = """
        namespace Lib;
        public class Widget
        {
            public int N { get; set; }
            public void M() { }
        }
        """;

    private static string ProjectIdOf(AdhocWorkspace workspace) =>
        workspace.CurrentSolution.Projects.Single().Id.Id.ToString("D");

    private static AdhocWorkspace CreateWorkspace(string source)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "Lib",
            "Lib",
            LanguageNames.CSharp,
            filePath: @"C:\fake\Lib.csproj"));
        solution = solution.AddDocument(docId, "Widget.cs", SourceText.From(source), filePath: @"C:\fake\Widget.cs");
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(
            projectId,
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace.");
        }

        return workspace;
    }

    private static AdhocWorkspace CreateGeneratorWorkspace()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "GeneratorHost",
            "GeneratorHost",
            LanguageNames.CSharp,
            filePath: @"C:\fake\GeneratorHost.csproj"));
        const string source = """
            namespace GeneratorHost;
            public static class Host { public static string Name => "host"; }
            public partial class PartialThing { public string Format() => "hw"; }
            """;
        solution = solution.AddDocument(
            docId,
            "Host.cs",
            SourceText.From(source),
            filePath: @"C:\fake\Host.cs");
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(
            projectId,
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        solution = solution.AddAnalyzerReference(
            projectId,
            new AnalyzerFileReference(
                typeof(CustomGenerator.MarkerGenerator).Assembly.Location,
                AnalyzerAssemblyLoader.Instance));
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace.");
        }

        return workspace;
    }

    private sealed class FakeSession : IWorkspaceSession
    {
        private readonly GeneratorRunCache _cache = new();
        private readonly bool _compilationUnavailable;

        public FakeSession(Solution solution, long epoch = 1, bool compilationUnavailable = false)
        {
            Solution = solution;
            Epoch = epoch;
            FSharpSnapshot = new FSharpWorkspaceSnapshot(epoch, []);
            _compilationUnavailable = compilationUnavailable;
        }

        public long Epoch { get; }

        public Solution Solution { get; }

        public FSharpWorkspaceSnapshot FSharpSnapshot { get; }

        public async Task<Compilation> GetCompilationAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default)
        {
            if (_compilationUnavailable)
            {
                throw new InvalidOperationException("Compilation was null.");
            }

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

    private sealed class AnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
    {
        public static AnalyzerAssemblyLoader Instance { get; } = new();

        public void AddDependencyLocation(string fullPath)
        {
        }

        public System.Reflection.Assembly LoadFromPath(string fullPath) =>
            System.Reflection.Assembly.LoadFrom(fullPath);
    }
}
