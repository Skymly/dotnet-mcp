using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class WorkspaceListFsharpProjectsSeamTests
{
    [Fact]
    public async Task workspace_list_projects_reports_fsharp_language_for_in_memory_fsharp_project()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFsharpAndCSharp());

            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var body = await ListProjectsAsync(fx);
            Assert.Contains(body.Projects, p => p.Name == "CsLib" && p.Language == "csharp");
            Assert.Contains(body.Projects, p => p.Name == "FsLib" && p.Language == "fsharp");
        }
        finally
        {
            TryDelete(root);
        }
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
