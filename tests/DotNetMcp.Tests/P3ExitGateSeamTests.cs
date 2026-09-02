using DotNetMcp.Server;

namespace DotNetMcp.Tests;

/// <summary>
/// P3 exit gate (#76): demoable F# / COM / dynamic MCP loop. No new product tools.
/// </summary>
public class P3ExitGateSeamTests
{
    [Fact]
    public async Task p3_fsharp_loop_open_list_resolve_navigate_refs_diagnostics()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFsharpSymbols(root));

            Assert.True((await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution })).IsError is not true);
            await OpenUntilReadyAsync(fx);

            var list = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(
                await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>()));
            Assert.Contains(list.Projects, p => p.Language == "fsharp");
            Assert.Contains(list.Projects, p => p.Language == "csharp");

            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "FsLib.Widget" });
            Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
            var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;
            Assert.StartsWith("fsharp:", handle, StringComparison.Ordinal);

            Assert.True((await fx.Client.CallToolAsync(
                "symbol_summary", new Dictionary<string, object?> { ["handle"] = handle })).IsError is not true);
            Assert.True((await fx.Client.CallToolAsync(
                "symbol_goto_definition", new Dictionary<string, object?> { ["handle"] = handle })).IsError is not true);
            Assert.True((await fx.Client.CallToolAsync(
                "symbol_members", new Dictionary<string, object?> { ["handle"] = handle })).IsError is not true);
            Assert.True((await fx.Client.CallToolAsync(
                "symbol_find_references", new Dictionary<string, object?> { ["handle"] = handle })).IsError is not true);

            var fsProject = Assert.Single(list.Projects, p => p.Language == "fsharp");
            Assert.True((await fx.Client.CallToolAsync(
                "project_diagnostics",
                new Dictionary<string, object?> { ["projectId"] = fsProject.ProjectId })).IsError is not true);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task p3_com_and_dynamic_loop()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        try
        {
            await using (var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithComInterop()))
            {
                Assert.True((await fx.Client.CallToolAsync(
                    "workspace_open",
                    new Dictionary<string, object?> { ["path"] = solution })).IsError is not true);
                await OpenUntilReadyAsync(fx);
                var resolved = await fx.Client.CallToolAsync(
                    "symbol_resolve",
                    new Dictionary<string, object?> { ["name"] = "ComLib.IComThing" });
                Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
                var body = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved);
                Assert.Equal("ComImport", body.Summary.InteropKind);
            }

            await using (var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithDynamic()))
            {
                Assert.True((await fx.Client.CallToolAsync(
                    "workspace_open",
                    new Dictionary<string, object?> { ["path"] = solution })).IsError is not true);
                await OpenUntilReadyAsync(fx);
                var list = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(
                    await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>()));
                var dyn = await fx.Client.CallToolAsync(
                    "project_list_dynamic_invocations",
                    new Dictionary<string, object?> { ["projectId"] = Assert.Single(list.Projects).ProjectId });
                Assert.True(dyn.IsError is not true, InProcessMcpFixture.TextOf(dyn));
                Assert.NotEmpty(InProcessMcpFixture.Deserialize<ProjectListDynamicInvocationsResultDto>(dyn).Items);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task OpenUntilReadyAsync(InProcessMcpFixture fx)
    {
        WorkspaceStatusDto? last = null;
        for (var i = 0; i < 400; i++)
        {
            var poll = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
            last = InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(poll);
            if (last.Phase is "ready" or "failed" or "cancelled")
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.NotNull(last);
        Assert.True(last!.Phase == "ready", $"workspace phase={last.Phase} error={last.Error}");
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
