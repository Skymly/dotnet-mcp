using DotNetMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

public class FSharpCompilePathPrioritySeamTests
{
    [Fact]
    public void expanded_documents_win_over_incomplete_compile_include()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-fsdoc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var apple = Path.Combine(dir, "Apple.fs");
            var zebra = Path.Combine(dir, "Zebra.fs");
            var onlyXml = Path.Combine(dir, "OnlyXml.fs");
            File.WriteAllText(apple, "module Apple\n");
            File.WriteAllText(zebra, "module Zebra\n");
            File.WriteAllText(onlyXml, "module OnlyXml\n");
            File.WriteAllText(Path.Combine(dir, "Lib.fsproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <Compile Include="OnlyXml.fs" />
                  </ItemGroup>
                </Project>
                """);

            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "Lib",
                "Lib",
                LanguageNames.FSharp,
                filePath: Path.Combine(dir, "Lib.fsproj")));
            solution = solution.AddDocument(DocumentId.CreateNewId(projectId), "Apple.fs", SourceText.From("module Apple\n"), filePath: apple);
            solution = solution.AddDocument(DocumentId.CreateNewId(projectId), "Zebra.fs", SourceText.From("module Zebra\n"), filePath: zebra);
            Assert.True(workspace.TryApplyChanges(solution));

            var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
            var snapshot = WorkspaceSession.CaptureFSharpSnapshot(loaded.Solution, epoch: 1, TrustedRoots.Create([dir]));
            var names = Assert.Single(snapshot.Projects).Documents.Select(d => Path.GetFileName(d.Path)).ToArray();
            Assert.Equal(new[] { "Apple.fs", "Zebra.fs" }, names);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
