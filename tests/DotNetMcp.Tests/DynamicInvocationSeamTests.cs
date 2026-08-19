using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class DynamicInvocationSeamTests
{
    [Fact]
    public async Task project_list_dynamic_invocations_lists_dynamic_calls_and_skips_static_ones()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithDynamic());
            await OpenReady(fx, solution);

            var list = await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>());
            var projectId = Assert.Single(InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list).Projects).ProjectId;

            var result = await fx.Client.CallToolAsync(
                "project_list_dynamic_invocations",
                new Dictionary<string, object?> { ["projectId"] = projectId });
            Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
            var body = InProcessMcpFixture.Deserialize<ProjectListDynamicInvocationsResultDto>(result);
            Assert.NotEmpty(body.Items);
            Assert.Contains(body.Items, i => i.Kind == "Invocation" && (i.FilePath ?? "").Contains("Host.cs", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(body.Items, i => i.ArgumentStaticTypes.Any(t => t == "int") && i.ArgumentStaticTypes.Any(t => t == "string"));
            Assert.DoesNotContain(body.Items, i => (i.FilePath ?? "").Contains("Host.cs", StringComparison.OrdinalIgnoreCase) && i.Kind == "Invocation" && i.ArgumentStaticTypes.Count == 0 && i.ReceiverStaticType == "int");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task tool_surface_includes_dynamic_invocations()
    {
        await using var fx = new InProcessMcpFixture();
        var tools = await fx.Client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "project_list_dynamic_invocations");
    }

    private static async Task OpenReady(InProcessMcpFixture fx, string path)
    {
        var open = await fx.Client.CallToolAsync("workspace_open", new Dictionary<string, object?> { ["path"] = path });
        Assert.True(open.IsError is not true);
        for (var i = 0; i < 80; i++)
        {
            var poll = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
            if (InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(poll).Phase == "ready") return;
            await Task.Delay(25);
        }
        Assert.Fail("not ready");
    }

    private static string CreateTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnet-mcp-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
