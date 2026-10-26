using DotNetMcp.Core;
using DotNetMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Protocol;

namespace DotNetMcp.Tests;

/// <summary>
/// Issue #548: symbol_find_callers surfaced a raw Roslyn exception as the generic MCP
/// invocation error. The trigger is an <see cref="UnresolvedAnalyzerReference"/> — the stub
/// MSBuildWorkspace leaves on a project when an &lt;Analyzer&gt; item cannot be resolved to a
/// file — because Roslyn's serializer cannot checksum it inside SymbolFinder's dependent-type
/// cascade. These tests pin the engine limitation, the load-time swap to a checksumable
/// image reference, the structured error mapping, and that client cancellation stays
/// cancellation.
/// </summary>
public class SymbolFindCallersErrorSeamTests
{
    private const string MissingAnalyzerPath = @"C:\missing\analyzers\Missing.Generator.dll";

    /// <summary>
    /// Engine evidence (Roslyn 5.9.0): DependentTypeFinder checksums each walked project's
    /// analyzer references and throws InvalidOperationException on an unresolved stub. If a
    /// future Roslyn makes these references checksumable this test fails and the strip in
    /// <see cref="MsBuildSolutionLoader"/> can be revisited.
    /// </summary>
    [Fact]
    public async Task find_callers_throws_inside_roslyn_on_unresolved_analyzer_reference()
    {
        await using var loaded = CreateUnresolvedAnalyzerLoaded();
        var service = new LanguageAdapters([new RoslynLanguageAdapter(new GeneratorQueryService())]);
        using var session = new WorkspaceSession(loaded, epoch: 1);
        var libProjectId = loaded.Solution.Projects
            .First(static p => p.Name == "SampleLib").Id.Id.ToString("D");

        var (resolved, resolveError) = await service.ResolveByNameAsync(
            session, "SampleLib.MathOps.Add", libProjectId);
        Assert.Null(resolveError);
        Assert.NotNull(resolved);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.FindCallersAsync(session, resolved!.Handle, limit: 50));
        Assert.Contains("UnresolvedAnalyzerReference", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The root-cause fix: the loader detaches unresolvable analyzer stubs — they carry no
    /// analyzers, only error-reporting metadata — so the dependent-type cascade never trips.
    /// The declared paths survive as <see cref="LoadedSolution.UnresolvedAnalyzerPaths"/> so
    /// project_list_generators still reports them (issue #551), and each is named in the
    /// load warnings. Swapping for an empty AnalyzerImageReference does NOT work — the
    /// serializer accepts images only under test GUIDs (Contract.Fail otherwise), pinned by
    /// this test's sibling.
    /// </summary>
    [Fact]
    public async Task load_strips_unresolved_analyzer_references_and_callers_completes()
    {
        var workspace = CreateUnresolvedAnalyzerWorkspace();
        var warnings = new List<string>();

        var unresolvedPaths = MsBuildSolutionLoader.RemoveUnresolvedAnalyzerReferences(workspace, warnings);

        Assert.DoesNotContain(
            workspace.CurrentSolution.Projects.SelectMany(static p => p.AnalyzerReferences),
            static r => r is UnresolvedAnalyzerReference);
        Assert.Contains(warnings, w => w.Contains("Missing.Generator.dll", StringComparison.OrdinalIgnoreCase));

        var appId = workspace.CurrentSolution.Projects.Single(static p => p.Name == "App").Id;
        Assert.NotNull(unresolvedPaths);
        Assert.Contains(unresolvedPaths![appId], p => p == MissingAnalyzerPath);

        await using var loaded = new LoadedSolution(
            workspace, workspace.CurrentSolution, warnings, unresolvedPaths);
        var service = new LanguageAdapters([new RoslynLanguageAdapter(new GeneratorQueryService())]);
        using var session = new WorkspaceSession(loaded, epoch: 1);
        var libProjectId = loaded.Solution.Projects
            .First(static p => p.Name == "SampleLib").Id.Id.ToString("D");
        var (resolved, resolveError) = await service.ResolveByNameAsync(
            session, "SampleLib.MathOps.Add", libProjectId);
        Assert.Null(resolveError);
        Assert.NotNull(resolved);

        var (page, error) = await service.FindCallersAsync(session, resolved!.Handle, limit: 50);
        Assert.Null(error);
        Assert.NotNull(page);
        Assert.Contains(page!.Items, i =>
            (i.FilePath ?? string.Empty).Contains("Uses.cs", StringComparison.OrdinalIgnoreCase));

        // #551 coexistence: the detached stub's declared path still reaches the tool surface
        // as unresolved — via session data, not a reference object the serializer chokes on.
        var appProjectId = loaded.Solution.Projects
            .First(static p => p.Name == "App").Id.Id.ToString("D");
        var (generators, generatorsError) = await new GeneratorQueryService()
            .ListGeneratorsAsync(session, appProjectId);
        Assert.Null(generatorsError);
        Assert.NotNull(generators);
        Assert.Empty(generators!.Generators);
        Assert.Contains(generators.UnresolvedAnalyzerReferences,
            p => p.Equals(MissingAnalyzerPath, StringComparison.Ordinal));
    }

    /// <summary>
    /// Defense in depth over the MCP seam: when the engine does throw, the tool returns a
    /// structured SymbolQueryFailed error — never the generic invocation error — with no
    /// stack trace in the payload, and the exception is written to the audit sink.
    /// </summary>
    [Fact]
    public async Task symbol_find_callers_maps_engine_failure_to_structured_error()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            var audit = new RecordingAuditLogger();
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                new FakeSolutionLoader(TimeSpan.Zero, CreateUnresolvedAnalyzerLoaded),
                auditLogger: audit);

            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);
            var projects = await fx.Client.CallToolAsync(
                "workspace_list_projects", new Dictionary<string, object?>());
            var lib = Assert.Single(
                InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(projects).Projects,
                static p => p.Name == "SampleLib");
            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?>
                {
                    ["name"] = "SampleLib.MathOps.Add",
                    ["projectId"] = lib.ProjectId
                });
            Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
            var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;

            var result = await fx.Client.CallToolAsync(
                "symbol_find_callers",
                new Dictionary<string, object?> { ["handle"] = handle });

            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.SymbolQueryFailed, body.Error);
            Assert.DoesNotContain("An error occurred invoking", InProcessMcpFixture.TextOf(result), StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", body.Message, StringComparison.Ordinal);
            Assert.Contains(audit.Snapshot(), e => e.Kind == "tool_failed" && e.ToolName == "symbol_find_callers");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// The same boundary covers the finder siblings that share the SymbolFinder cascade path,
    /// and an adapter-thrown cancellation is never rewritten as a tool failure.
    /// </summary>
    [Fact]
    public async Task finders_map_adapter_failure_and_keep_cancellation_as_cancellation()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            var audit = new RecordingAuditLogger();
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithCallers(),
                auditLogger: audit);
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var roots = TrustedRoots.Create([root]);
            var handle = SymbolHandle.Create(
                LanguageAdapters.CSharpLanguage,
                Guid.NewGuid().ToString("D"),
                "SampleLib.MathOps.Add(int, int)").Format();
            var failing = new SymbolTools(
                fx.WorkspaceHost,
                fx.WorkspaceEdit,
                new LanguageAdapters([new ThrowingAdapter(new InvalidOperationException("adapter boom"))]),
                roots,
                audit);

            foreach (var call in Calls(failing, handle))
            {
                var result = await call();
                Assert.True(result.IsError is true);
                var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
                Assert.Equal(PolicyErrorCodes.SymbolQueryFailed, body.Error);
                Assert.Contains("adapter boom", body.Message, StringComparison.Ordinal);
            }

            Assert.Equal(3, audit.Snapshot().Count(static e => e.Kind == "tool_failed"));

            var cancelling = new SymbolTools(
                fx.WorkspaceHost,
                fx.WorkspaceEdit,
                new LanguageAdapters([new ThrowingAdapter(new OperationCanceledException())]),
                roots,
                audit);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => cancelling.SymbolFindCallers(handle));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static Func<Task<CallToolResult>>[] Calls(SymbolTools tools, string handle) =>
    [
        () => tools.SymbolFindCallers(handle),
        () => tools.SymbolFindReferences(handle),
        () => tools.SymbolFindImplementations(handle),
    ];

    /// <summary>
    /// A lib project plus a caller project; the caller carries the poison — an
    /// <see cref="UnresolvedAnalyzerReference"/>, exactly what MSBuildWorkspace emits for an
    /// &lt;Analyzer&gt; item whose file cannot be resolved.
    /// </summary>
    private static AdhocWorkspace CreateUnresolvedAnalyzerWorkspace()
    {
        var workspace = new AdhocWorkspace();
        var libId = ProjectId.CreateNewId();
        var appId = ProjectId.CreateNewId();

        const string libSource = """
            namespace SampleLib;

            public static class MathOps
            {
                public static int Add(int a, int b) => a + b;
            }
            """;

        const string appSource = """
            namespace App;

            public static class Uses
            {
                public static int One() => SampleLib.MathOps.Add(1, 2);
            }
            """;

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                libId, VersionStamp.Create(), "SampleLib", "SampleLib",
                LanguageNames.CSharp, filePath: @"C:\fake\SampleLib\SampleLib.csproj"))
            .AddProject(ProjectInfo.Create(
                appId, VersionStamp.Create(), "App", "App",
                LanguageNames.CSharp, filePath: @"C:\fake\App\App.csproj"));
        solution = solution.AddDocument(
            DocumentId.CreateNewId(libId), "MathOps.cs", SourceText.From(libSource),
            filePath: @"C:\fake\SampleLib\MathOps.cs");
        solution = solution.AddDocument(
            DocumentId.CreateNewId(appId), "Uses.cs", SourceText.From(appSource),
            filePath: @"C:\fake\App\Uses.cs");
        solution = solution.AddProjectReference(appId, new ProjectReference(libId));

        foreach (var pid in new[] { libId, appId })
        {
            solution = solution.WithProjectCompilationOptions(
                pid, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            foreach (var reference in TrustedPlatformReferences())
            {
                solution = solution.AddMetadataReference(pid, reference);
            }
        }

        solution = solution.AddAnalyzerReference(appId, new UnresolvedAnalyzerReference(MissingAnalyzerPath));

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply unresolved-analyzer fixture.");
        }

        return workspace;
    }

    private static LoadedSolution CreateUnresolvedAnalyzerLoaded()
    {
        var workspace = CreateUnresolvedAnalyzerWorkspace();
        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    private static IReadOnlyList<MetadataReference> TrustedPlatformReferences()
    {
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return tpa
            .Where(p => Path.GetFileName(p) is "System.Private.CoreLib.dll" or "System.Runtime.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
    }

    private sealed class ThrowingAdapter(Exception exception) : ILanguageAdapter
    {
        public bool OwnsLanguage(string languageToken) => true;

        public bool OwnsProject(Project project) => true;

        public bool SupportsCodeRefactoring => false;

        public bool SupportsDiagnosticFix => false;

        public bool SupportsSourceGenerators => false;

        public bool SupportsDynamicInvocations => false;

        public Task<(SymbolResolveSuccess? Success, SymbolQueryError? Error)> ResolveByNameAsync(
            IWorkspaceSession session, string name, string? projectId = null,
            CancellationToken cancellationToken = default) => throw exception;

        public Task<(SymbolResolveSuccess? Success, SymbolQueryError? Error)> GetSummaryAsync(
            IWorkspaceSession session, string handle,
            CancellationToken cancellationToken = default) => throw exception;

        public Task<(SymbolResolveAtSuccess? Success, SymbolQueryError? Error)> ResolveAtPositionAsync(
            IWorkspaceSession session, string path, int line, int column, string? projectId = null,
            CancellationToken cancellationToken = default) => throw exception;

        public Task<(SymbolDefinitionSuccess? Success, SymbolQueryError? Error)> GetDefinitionAsync(
            IWorkspaceSession session, string handle,
            CancellationToken cancellationToken = default) => throw exception;

        public Task<(SymbolAttributionSuccess? Success, SymbolQueryError? Error)> GetAttributionAsync(
            IWorkspaceSession session, string handle,
            CancellationToken cancellationToken = default) => throw exception;

        public Task<(PagedResult<MemberListItem>? Success, SymbolQueryError? Error)> GetMembersAsync(
            IWorkspaceSession session, string handle, int? limit = null, string? cursor = null,
            CancellationToken cancellationToken = default) => throw exception;

        public Task<(PagedResult<ReferenceLocationItem>? Success, SymbolQueryError? Error)> FindReferencesAsync(
            IWorkspaceSession session, string handle, bool entireSolution = false, int? limit = null,
            string? cursor = null, TimeSpan? softBudget = null,
            CancellationToken cancellationToken = default) => throw exception;

        public Task<(PagedResult<ImplementationItem>? Success, SymbolQueryError? Error)> FindImplementationsAsync(
            IWorkspaceSession session, string handle, int? limit = null, string? cursor = null,
            CancellationToken cancellationToken = default) => throw exception;

        public Task<(PagedResult<HierarchyItem>? Success, SymbolQueryError? Error)> GetTypeHierarchyAsync(
            IWorkspaceSession session, string handle, int? limit = null, string? cursor = null,
            CancellationToken cancellationToken = default) => throw exception;

        public Task<(PagedResult<CallerLocationItem>? Success, SymbolQueryError? Error)> FindCallersAsync(
            IWorkspaceSession session, string handle, bool entireSolution = false, int? limit = null,
            string? cursor = null, TimeSpan? softBudget = null,
            CancellationToken cancellationToken = default) => throw exception;

        public Task<(SymbolSourceSuccess? Success, SymbolQueryError? Error)> GetSourceAsync(
            IWorkspaceSession session, string handle, int maxChars,
            CancellationToken cancellationToken = default) => throw exception;

        public Task<(ProjectDiagnosticsResult? Success, SymbolQueryError? Error)> GetProjectDiagnosticsAsync(
            IWorkspaceSession session, string projectId, int? limit = null, string? cursor = null,
            TimeSpan? softBudget = null, CancellationToken cancellationToken = default) => throw exception;

        public Task<(RenamePreviewDraft? Draft, SymbolQueryError? Error)> BuildRenamePreviewAsync(
            IWorkspaceSession session, string handle, string newName,
            CancellationToken cancellationToken = default) => throw exception;
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
            // best-effort cleanup
        }
    }
}
