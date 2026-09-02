using DotNetMcp.Core;
using DotNetMcp.FSharp;
using DotNetMcp.Server;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Tests;

public class LanguageAdapterSeamTests
{
    [Fact]
    public async Task mixed_workspace_accepts_fsharp_project_id_on_diagnostics_not_only_resolve()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFsharpSymbols(root));

            await OpenUntilReadyAsync(fx, solution);

            var list = await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>());
            var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list);
            var fs = Assert.Single(projects.Projects, p => p.Language == "fsharp");

            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?>
                {
                    ["name"] = "FsLib.Widget",
                    ["projectId"] = fs.ProjectId
                });
            Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
            var body = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved);
            Assert.StartsWith("fsharp:", body.Handle, StringComparison.Ordinal);

            var diagnostics = await fx.Client.CallToolAsync(
                "project_diagnostics",
                new Dictionary<string, object?> { ["projectId"] = fs.ProjectId });
            Assert.True(diagnostics.IsError is not true, InProcessMcpFixture.TextOf(diagnostics));
            var page = InProcessMcpFixture.Deserialize<ProjectDiagnosticsResultDto>(diagnostics);
            Assert.All(page.Items, d => Assert.Equal(fs.ProjectId, d.ProjectId));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task language_adapters_select_once_so_a_third_adapter_is_reached_without_copied_ifs()
    {
        var root = CreateTempDir("fake");
        try
        {
            var loaded = FakeSolutionLoader.CreateFsharpSymbolsLoaded(root);
            var fake = new FakeLanguageAdapter();
            var service = new LanguageAdapters([new RoslynLanguageAdapter(new GeneratorQueryService()), fake]);
            using var session = new WorkspaceSession(loaded, epoch: 1);

            var handle = SymbolHandle.Create("fake", Guid.NewGuid().ToString("D"), "Fake.Type").Format();
            var (success, error) = await service.GetSummaryAsync(session, handle);
            Assert.Null(error);
            Assert.NotNull(success);
            Assert.Equal("from-fake", success!.Summary.DisplayName);
            Assert.Equal(1, fake.SummaryCalls);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task wrong_language_handle_does_not_silently_use_the_other_adapter()
    {
        var root = CreateTempDir("wrong");
        try
        {
            var loaded = FakeSolutionLoader.CreateFsharpSymbolsLoaded(root);
            var roslyn = new RoslynLanguageAdapter(new GeneratorQueryService());
            var fsharp = new FSharpSymbolQueryService();
            var languages = new LanguageAdapters([roslyn, fsharp]);
            using var session = new WorkspaceSession(loaded, epoch: 1);

            var fs = loaded.Solution.Projects.Single(p => p.Language == LanguageNames.FSharp);
            var cs = loaded.Solution.Projects.Single(p => p.Language == LanguageNames.CSharp);
            var fsharpHandle = SymbolHandle.Create(
                LanguageAdapters.FSharpLanguage,
                fs.Id.Id.ToString("D"),
                "FsLib.Widget").Format();
            var csharpHandle = SymbolHandle.Create(
                LanguageAdapters.CSharpLanguage,
                cs.Id.Id.ToString("D"),
                "CsLib.Caller").Format();

            Assert.Same(roslyn, languages.TryGet(LanguageAdapters.CSharpLanguage, out var roslynSelected) ? roslynSelected : null);
            Assert.Same(fsharp, languages.TryGet(LanguageAdapters.FSharpLanguage, out var fsharpSelected) ? fsharpSelected : null);
            Assert.Same(fsharp, languages.ForProjectId(loaded.Solution, fs.Id.Id.ToString("D")));
            Assert.Same(roslyn, languages.ForProjectId(loaded.Solution, cs.Id.Id.ToString("D")));

            var (roslynOnFsharp, roslynError) = await roslyn.GetSummaryAsync(session, fsharpHandle);
            Assert.Null(roslynOnFsharp);
            Assert.IsType<InvalidSymbolHandleError>(roslynError);
            Assert.Contains("fsharp", roslynError!.Message, StringComparison.OrdinalIgnoreCase);

            var (fsharpOnCsharp, fsharpError) = await fsharp.GetSummaryAsync(session, csharpHandle);
            Assert.Null(fsharpOnCsharp);
            Assert.IsType<InvalidSymbolHandleError>(fsharpError);
            Assert.Contains("csharp", fsharpError!.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void core_query_modules_do_not_copy_fsharp_language_dispatch()
    {
        var coreDir = FindCoreDir();
        Assert.False(File.Exists(Path.Combine(coreDir, "SymbolQueryService.cs")));
        Assert.False(File.Exists(Path.Combine(coreDir, "RenamePreviewService.cs")));

        var files = new[]
        {
            "LanguageAdapters.cs",
            "DiagnosticQueryService.cs",
            "CodeRefactoringService.cs",
            "DiagnosticFixService.cs",
        };

        foreach (var name in files)
        {
            var text = File.ReadAllText(Path.Combine(coreDir, name));
            Assert.DoesNotContain("IsFSharpHandle", text, StringComparison.Ordinal);
            Assert.DoesNotContain("IFSharpSymbolQuery", text, StringComparison.Ordinal);
            Assert.DoesNotContain("_fsharp", text, StringComparison.Ordinal);
            Assert.DoesNotContain("LanguageNames.FSharp", text, StringComparison.Ordinal);
        }
    }


    [Fact]
    public void dto_facade_is_language_adapters_not_a_pass_through_hop()
    {
        var coreDir = FindCoreDir();
        Assert.False(File.Exists(Path.Combine(coreDir, "SymbolQueryService.cs")));
        Assert.False(File.Exists(Path.Combine(coreDir, "RenamePreviewService.cs")));

        var languageAdapters = File.ReadAllText(Path.Combine(coreDir, "LanguageAdapters.cs"));
        Assert.Contains("GetSummaryAsync", languageAdapters, StringComparison.Ordinal);
        Assert.Contains("BuildRenamePreviewAsync", languageAdapters, StringComparison.Ordinal);

        var repo = Directory.GetParent(coreDir)!.Parent!;
        var files = new[]
        {
            Path.Combine(repo.FullName, "src", "DotNetMcp.Server", "SymbolTools.cs"),
            Path.Combine(repo.FullName, "src", "DotNetMcp.Server", "ServerHost.cs"),
            Path.Combine(repo.FullName, "src", "DotNetMcp.Xaml", "XamlDocumentService.cs"),
            Path.Combine(coreDir, "CodeRefactoringService.cs"),
        };
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("SymbolQueryService ", text, StringComparison.Ordinal);
            Assert.DoesNotContain("RenamePreviewService", text, StringComparison.Ordinal);
        }
    }
    private static string FindCoreDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "DotNetMcp.Core");
            if (File.Exists(Path.Combine(candidate, "LanguageAdapters.cs")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate src/DotNetMcp.Core from the test assembly.");
    }

    private static async Task OpenUntilReadyAsync(InProcessMcpFixture fx, string path)
    {
        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = path });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        WorkspaceStatusDto? last = null;
        for (var i = 0; i < 400; i++)
        {
            var poll = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
            last = InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(poll);
            if (last.Phase is "ready" or "failed")
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.True(last?.Phase == "ready", $"phase={last?.Phase} error={last?.Error} message={last?.Message}");
    }

    private static string CreateTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnet-mcp-lang-{label}-{Guid.NewGuid():N}");
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

    private sealed class FakeLanguageAdapter : ILanguageAdapter
    {
        public int SummaryCalls { get; private set; }

        public bool OwnsLanguage(string languageToken) =>
            string.Equals(languageToken, "fake", StringComparison.Ordinal);

        public bool OwnsProject(Project project) => false;

        public bool SupportsCodeRefactoring => false;

        public bool SupportsDiagnosticFix => false;

        public Task<(SymbolResolveSuccess? Success, SymbolQueryError? Error)> ResolveByNameAsync(
            IWorkspaceSession session,
            string name,
            string? projectId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<(SymbolResolveSuccess?, SymbolQueryError?)>((null, NotFound()));

        public Task<(SymbolResolveSuccess? Success, SymbolQueryError? Error)> GetSummaryAsync(
            IWorkspaceSession session,
            string handle,
            CancellationToken cancellationToken = default)
        {
            SummaryCalls++;
            var parsedOk = SymbolHandle.TryParse(handle, out var parsed, out _);
            Assert.True(parsedOk);
            var success = new SymbolResolveSuccess(
                handle,
                new SymbolSummary(
                    Kind: "NamedType",
                    DisplayName: "from-fake",
                    ContainingSymbol: null,
                    Accessibility: "Public",
                    ProjectId: parsed!.ProjectId,
                    Language: "fake"));
            return Task.FromResult<(SymbolResolveSuccess?, SymbolQueryError?)>((success, null));
        }

        public Task<(SymbolDefinitionSuccess? Success, SymbolQueryError? Error)> GetDefinitionAsync(
            IWorkspaceSession session,
            string handle,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<(SymbolDefinitionSuccess?, SymbolQueryError?)>((null, NotFound()));

        public Task<(PagedResult<MemberListItem>? Success, SymbolQueryError? Error)> GetMembersAsync(
            IWorkspaceSession session,
            string handle,
            int? limit = null,
            string? cursor = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<(PagedResult<MemberListItem>?, SymbolQueryError?)>((null, NotFound()));

        public Task<(PagedResult<ReferenceLocationItem>? Success, SymbolQueryError? Error)> FindReferencesAsync(
            IWorkspaceSession session,
            string handle,
            bool entireSolution = false,
            int? limit = null,
            string? cursor = null,
            TimeSpan? softBudget = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<(PagedResult<ReferenceLocationItem>?, SymbolQueryError?)>((null, NotFound()));

        public Task<(PagedResult<ImplementationItem>? Success, SymbolQueryError? Error)> FindImplementationsAsync(
            IWorkspaceSession session,
            string handle,
            int? limit = null,
            string? cursor = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<(PagedResult<ImplementationItem>?, SymbolQueryError?)>((null, NotFound()));

        public Task<(PagedResult<HierarchyItem>? Success, SymbolQueryError? Error)> GetTypeHierarchyAsync(
            IWorkspaceSession session,
            string handle,
            int? limit = null,
            string? cursor = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<(PagedResult<HierarchyItem>?, SymbolQueryError?)>((null, NotFound()));

        public Task<(PagedResult<CallerLocationItem>? Success, SymbolQueryError? Error)> FindCallersAsync(
            IWorkspaceSession session,
            string handle,
            int? limit = null,
            string? cursor = null,
            TimeSpan? softBudget = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<(PagedResult<CallerLocationItem>?, SymbolQueryError?)>((null, NotFound()));


        public Task<(SymbolAttributionSuccess? Success, SymbolQueryError? Error)> GetAttributionAsync(
            IWorkspaceSession session,
            string handle,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<(SymbolAttributionSuccess?, SymbolQueryError?)>((null, NotFound()));

        public Task<(PagedResult<DiagnosticItem>? Success, SymbolQueryError? Error)> GetProjectDiagnosticsAsync(
            IWorkspaceSession session,
            string projectId,
            int? limit = null,
            string? cursor = null,
            TimeSpan? softBudget = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<(PagedResult<DiagnosticItem>?, SymbolQueryError?)>((null, NotFound()));

        public Task<(RenamePreviewDraft? Draft, SymbolQueryError? Error)> BuildRenamePreviewAsync(
            IWorkspaceSession session,
            string handle,
            string newName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<(RenamePreviewDraft?, SymbolQueryError?)>((null, NotFound()));

        private static SymbolNotFoundError NotFound() =>
            new("Fake adapter has no symbols.", "Do not call this adapter for real queries.");
    }
}
