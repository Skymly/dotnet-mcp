using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class WorkspaceListVbProjectsSeamTests
{
    [Fact]
    public async Task workspace_list_projects_reports_csharp_language_on_existing_tfm_rows()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateMultiTfm());

            await OpenReadyAsync(fx, solution);

            var body = await ListProjectsAsync(fx);
            Assert.Equal(2, body.Projects.Count);
            Assert.All(body.Projects, p => Assert.Equal("csharp", p.Language));
            Assert.Contains(body.Projects, p => p.TargetFramework == "net8.0");
            Assert.Contains(body.Projects, p => p.TargetFramework == "net9.0");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task workspace_list_projects_reports_vb_language_for_in_memory_vb_project()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithVbAndCSharp());

            await OpenReadyAsync(fx, solution);

            var body = await ListProjectsAsync(fx);
            Assert.Contains(body.Projects, p => p.Name == "CsLib" && p.Language == "csharp");
            Assert.Contains(body.Projects, p => p.Name == "VbLib" && p.Language == "vb");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task OpenReadyAsync(InProcessMcpFixture fx, string path)
    {
        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = path });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        WorkspaceStatusDto? status = null;
        for (var i = 0; i < 40; i++)
        {
            var poll = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
            status = InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(poll);
            if (status.Phase == "ready")
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Workspace did not become ready: {status?.Phase}");
    }

    private static async Task<WorkspaceListProjectsResultDto> ListProjectsAsync(InProcessMcpFixture fx)
    {
        var list = await fx.Client.CallToolAsync(
            "workspace_list_projects",
            new Dictionary<string, object?>());
        Assert.True(list.IsError is not true, InProcessMcpFixture.TextOf(list));
        return InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list);
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
