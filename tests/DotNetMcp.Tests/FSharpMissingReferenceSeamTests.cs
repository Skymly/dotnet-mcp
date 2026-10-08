using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class FSharpMissingReferenceSeamTests
{
    [Fact]
    public async Task unbuilt_project_reference_is_reported_instead_of_only_fs0039()
    {
        var source = Path.Combine(MsBuildWorkspaceIntegrationTests.FixturesRoot, "FsharpProjectRef");
        Assert.True(Directory.Exists(source));
        var root = Path.Combine(Path.GetTempPath(), "dotnet-mcp-fsref-" + Guid.NewGuid().ToString("N"));
        Copy(source, root);
        var fsproj = Path.Combine(root, "FsApp", "FsApp.fsproj");
        try
        {
            Assert.False(Directory.Exists(Path.Combine(root, "CsDep", "bin")));
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                solutionLoader: null);
            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = fsproj });
            Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
            await WorkspaceReady.WaitUntilReadyAsync(fx, WorkspaceReady.MsBuildTimeout);

            var listed = await fx.Client.CallToolAsync(
                "workspace_list_projects",
                new Dictionary<string, object?>());
            var fsId = Assert.Single(
                InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(listed).Projects,
                p => p.Name.Contains("FsApp", StringComparison.OrdinalIgnoreCase)).ProjectId;

            var diagnostics = await fx.Client.CallToolAsync(
                "project_diagnostics",
                new Dictionary<string, object?> { ["projectId"] = fsId });
            Assert.True(diagnostics.IsError is not true, InProcessMcpFixture.TextOf(diagnostics));
            var page = InProcessMcpFixture.Deserialize<ProjectDiagnosticsResultDto>(diagnostics);
            Assert.Contains(page.Items, d => d.Id == "DependencyOutputNotBuilt");
            Assert.Contains(page.MissingDependencyOutputs ?? [], name =>
                name.Contains("CsDep", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void Copy(string source, string dest)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, dest, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(source, dest, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
