using DotNetMcp.Server;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Tests;

public class GraphGateDisposeTests
{
    [Fact]
    public async Task rejected_loaded_graph_disposes_the_workspace()
    {
        var root = CreateTempDir("root");
        var outside = CreateTempDir("outside");
        var outsideProj = Path.Combine(outside, "Evil.csproj");
        await File.WriteAllTextAsync(outsideProj, "<Project></Project>");

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        Assert.True(workspace.TryApplyChanges(workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "Evil",
            "Evil",
            LanguageNames.CSharp,
            filePath: outsideProj))));
        var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, []);

        try
        {
            await Assert.ThrowsAsync<LoadedGraphOutsideTrustedRootsException>(() =>
                MsBuildSolutionLoader.EnsureGraphOrDisposeAsync(loaded, TrustedRoots.Create([root])));
            Assert.True(loaded.IsDisposed);
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    private static string CreateTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), "dotnet-mcp-gate-" + label + "-" + Guid.NewGuid().ToString("N"));
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
