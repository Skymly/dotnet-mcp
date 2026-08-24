using DotNetMcp.Core;
using DotNetMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

public class FsharpSnapshotDiskSeamTests
{
    [Fact]
    public void capture_fsharp_reads_widget_from_fsproj_directory()
    {
        var fsproj = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures", "MixedCsharpVb", "FsLib", "FsLib.fsproj"));
        Assert.True(File.Exists(fsproj), fsproj);
        var widget = Path.Combine(Path.GetDirectoryName(fsproj)!, "Widget.fs");
        Assert.True(File.Exists(widget), widget);

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "FsLib",
            "FsLib",
            LanguageNames.FSharp,
            filePath: fsproj));
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("apply failed");
        }

        var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        using var session = new WorkspaceSession(loaded, epoch: 1);
        var docs = session.FSharpSnapshot.Projects.SelectMany(p => p.Documents).Select(d => d.Path).ToArray();
        Assert.True(docs.Any(p => p.EndsWith("Widget.fs", StringComparison.OrdinalIgnoreCase)),
            "docs=" + string.Join(";", docs) + " fp=" + workspace.CurrentSolution.GetProject(projectId)?.FilePath);

    }
}
