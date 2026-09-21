using System.Diagnostics;
using DotNetMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

public class WorkspaceLoadSeamTests
{
    [Fact]
    public async Task workspace_open_returns_immediately_while_load_still_running()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        var delay = TimeSpan.FromMilliseconds(800);
        var loader = FakeSolutionLoader.DelayedMultiTfm(delay);

        try
        {
            await using var fx = new InProcessMcpFixture(TrustedRoots.Create([root]), loader);
            var sw = Stopwatch.StartNew();
            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            sw.Stop();

            Assert.True(open.IsError is not true);
            Assert.True(sw.Elapsed < delay, $"open blocked for {sw.Elapsed}; expected << {delay}");

            var body = InProcessMcpFixture.Deserialize<WorkspaceOpenResultDto>(open);
            Assert.Equal("loading", body.Phase);
            Assert.Contains("workspace_status", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("do not retry workspace_open", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task workspace_status_reaches_ready_after_open()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.DelayedMultiTfm(TimeSpan.FromMilliseconds(150)));

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true);

            var status = await WorkspaceReady.WaitUntilReadyAsync(fx);
            Assert.True(status.CompletedUnits >= 1);
            Assert.True(status.TotalUnits >= 1);
            Assert.Contains("Proceed", status.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task workspace_list_projects_errors_with_workspace_not_ready_while_loading()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.DelayedMultiTfm(TimeSpan.FromMilliseconds(1000)));

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true);

            var list = await fx.Client.CallToolAsync(
                "workspace_list_projects",
                new Dictionary<string, object?>());

            Assert.True(list.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(list);
            Assert.Equal(PolicyErrorCodes.WorkspaceNotReady, body.Error);
            Assert.Contains("workspace_status", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("do not retry workspace_open", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task workspace_list_projects_returns_one_row_per_tfm_when_ready()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateMultiTfm());

            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var list = await fx.Client.CallToolAsync(
                "workspace_list_projects",
                new Dictionary<string, object?>());
            Assert.True(list.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list);

            Assert.Equal(2, body.Projects.Count);
            Assert.Contains(body.Projects, p => p.Name.Contains("net8.0", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(body.Projects, p => p.Name.Contains("net9.0", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(body.Projects, p => p.TargetFramework == "net8.0");
            Assert.Contains(body.Projects, p => p.TargetFramework == "net9.0");
            Assert.Equal(2, body.Projects.Select(p => p.ProjectId).Distinct().Count());
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task workspace_list_projects_idle_suggests_workspace_open()
    {
        var root = CreateTempDir("root");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateMultiTfm());

            var list = await fx.Client.CallToolAsync(
                "workspace_list_projects",
                new Dictionary<string, object?>());
            Assert.True(list.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(list);
            Assert.Equal(PolicyErrorCodes.WorkspaceNotReady, body.Error);
            Assert.Contains("workspace_open", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("poll until", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task workspace_open_graph_outside_roots_is_loaded_graph_error()
    {
        var root = CreateTempDir("root");
        var outside = CreateTempDir("outside");
        var outsideProj = Path.Combine(outside, "Evil.csproj");
        await File.WriteAllTextAsync(outsideProj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var insideProj = Path.Combine(root, "App.csproj");
        await File.WriteAllTextAsync(insideProj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        LoadedSolution Factory()
        {
            var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();
            var insideId = Microsoft.CodeAnalysis.ProjectId.CreateNewId();
            var outsideId = Microsoft.CodeAnalysis.ProjectId.CreateNewId();
            workspace.AddProject(Microsoft.CodeAnalysis.ProjectInfo.Create(
                insideId,
                Microsoft.CodeAnalysis.VersionStamp.Create(),
                "App",
                "App",
                Microsoft.CodeAnalysis.LanguageNames.CSharp,
                filePath: insideProj,
                projectReferences: [new Microsoft.CodeAnalysis.ProjectReference(outsideId)]));
            workspace.AddProject(Microsoft.CodeAnalysis.ProjectInfo.Create(
                outsideId,
                Microsoft.CodeAnalysis.VersionStamp.Create(),
                "Evil",
                "Evil",
                Microsoft.CodeAnalysis.LanguageNames.CSharp,
                filePath: outsideProj));
            return new LoadedSolution(workspace, workspace.CurrentSolution, []);
        }

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                new FakeSolutionLoader(TimeSpan.Zero, Factory));

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

            var status = await WaitUntilFailedAsync(fx);
            Assert.Equal(PolicyErrorCodes.LoadedGraphOutsideTrustedRoots, status.ErrorCode);
            Assert.Contains("trusted root", status.SuggestedAction, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("poll load progress", status.SuggestedAction, StringComparison.OrdinalIgnoreCase);

            var list = await fx.Client.CallToolAsync(
                "workspace_list_projects",
                new Dictionary<string, object?>());
            Assert.True(list.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(list);
            Assert.Equal(PolicyErrorCodes.LoadedGraphOutsideTrustedRoots, body.Error);
            Assert.Contains("trusted root", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("poll until", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    [Fact]
    public async Task workspace_open_on_disk_document_outside_roots_is_loaded_graph_error()
    {
        var root = CreateTempDir("root");
        var outside = CreateTempDir("outside");
        var outsideCs = Path.Combine(outside, "Evil.cs");
        await File.WriteAllTextAsync(outsideCs, "namespace Evil; public class Leak {}");
        var insideProj = Path.Combine(root, "App.csproj");
        await File.WriteAllTextAsync(insideProj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        LoadedSolution Factory()
        {
            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var docId = DocumentId.CreateNewId(projectId);
            var loaded = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "App",
                "App",
                LanguageNames.CSharp,
                filePath: insideProj));
            loaded = loaded.AddDocument(docId, "Evil.cs", SourceText.From("namespace Evil; public class Leak {}"), filePath: outsideCs);
            if (!workspace.TryApplyChanges(loaded))
            {
                throw new InvalidOperationException("Failed to apply out-of-root document fixture.");
            }

            return new LoadedSolution(workspace, workspace.CurrentSolution, []);
        }

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                new FakeSolutionLoader(TimeSpan.Zero, Factory));

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

            var status = await WaitUntilFailedAsync(fx);
            Assert.Equal(PolicyErrorCodes.LoadedGraphOutsideTrustedRoots, status.ErrorCode);

            var list = await fx.Client.CallToolAsync(
                "workspace_list_projects",
                new Dictionary<string, object?>());
            Assert.True(list.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(list);
            Assert.Equal(PolicyErrorCodes.LoadedGraphOutsideTrustedRoots, body.Error);
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    [Fact]
    public async Task workspace_open_analyzer_outside_roots_is_loaded_graph_error()

    {
        var root = CreateTempDir("root");
        var outside = CreateTempDir("outside");
        var dllSrc = Path.Combine(AppContext.BaseDirectory, "CustomGenerator.dll");
        Assert.True(File.Exists(dllSrc), $"Missing CustomGenerator.dll next to tests: {dllSrc}");
        var outsideDll = Path.Combine(outside, "CustomGenerator.dll");
        File.Copy(dllSrc, outsideDll);

        var insideProj = Path.Combine(root, "App.csproj");
        await File.WriteAllTextAsync(insideProj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        LoadedSolution Factory()
        {
            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            workspace.AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "App",
                "App",
                LanguageNames.CSharp,
                filePath: insideProj));
            var solution = workspace.CurrentSolution.AddAnalyzerReference(
                projectId,
                new AnalyzerFileReference(outsideDll, SeamAnalyzerAssemblyLoader.Instance));
            if (!workspace.TryApplyChanges(solution))
            {
                throw new InvalidOperationException("Failed to apply out-of-root analyzer fixture.");
            }

            return new LoadedSolution(workspace, workspace.CurrentSolution, []);
        }

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                new FakeSolutionLoader(TimeSpan.Zero, Factory));

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

            var status = await WaitUntilFailedAsync(fx);
            Assert.Equal(PolicyErrorCodes.LoadedGraphOutsideTrustedRoots, status.ErrorCode);
            Assert.DoesNotContain(outsideDll, status.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(outsideDll, status.SuggestedAction ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            var list = await fx.Client.CallToolAsync(
                "workspace_list_projects",
                new Dictionary<string, object?>());
            Assert.True(list.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(list);
            Assert.Equal(PolicyErrorCodes.LoadedGraphOutsideTrustedRoots, body.Error);
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    [Fact]
    public async Task workspace_open_generic_load_failure_is_not_loaded_graph_error()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                new FakeSolutionLoader(TimeSpan.Zero, () => throw new InvalidOperationException("MSBuild exploded")));

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

            var status = await WaitUntilFailedAsync(fx);
            Assert.Null(status.ErrorCode);
            Assert.Contains("MSBuild exploded", status.Error, StringComparison.Ordinal);

            var list = await fx.Client.CallToolAsync(
                "workspace_list_projects",
                new Dictionary<string, object?>());
            Assert.True(list.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(list);
            Assert.Equal(PolicyErrorCodes.WorkspaceNotReady, body.Error);
            Assert.DoesNotContain("poll until", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<WorkspaceStatusDto> WaitUntilFailedAsync(InProcessMcpFixture fx)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        WorkspaceStatusDto? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var poll = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
            Assert.True(poll.IsError is not true, InProcessMcpFixture.TextOf(poll));
            last = InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(poll);
            if (last.Phase == "failed")
            {
                return last;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"workspace did not fail: phase={last?.Phase} error={last?.Error}");
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

    private sealed class SeamAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
    {
        public static SeamAnalyzerAssemblyLoader Instance { get; } = new();

        public void AddDependencyLocation(string fullPath)
        {
        }

        public System.Reflection.Assembly LoadFromPath(string fullPath) =>
            System.Reflection.Assembly.LoadFrom(fullPath);
    }
}
