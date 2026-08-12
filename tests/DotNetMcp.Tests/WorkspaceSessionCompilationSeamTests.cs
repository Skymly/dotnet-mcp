using DotNetMcp.Core;
using DotNetMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

public class WorkspaceSessionCompilationSeamTests
{
    [Fact]
    public async Task get_compilation_reuses_same_instance_within_session_lru()
    {
        var loaded = FakeSolutionLoader.CreateSymbolsLoaded(@"C:\fake\SampleLib.csproj");
        var projectId = loaded.Solution.Projects.Single().Id;
        using var session = new WorkspaceSession(loaded, epoch: 1, compilationLruCapacity: 50);

        var first = await session.GetCompilationAsync(projectId);
        var second = await session.GetCompilationAsync(projectId);

        Assert.Same(first, second);
        Assert.Equal(0, session.CompilationCache.Evictions);
        Assert.Equal(1, session.CompilationCache.Count);
    }

    [Fact]
    public async Task compilation_lru_evicts_oldest_when_capacity_is_one()
    {
        var loaded = FakeSolutionLoader.CreateMultiTfmLoaded(@"C:\fake\Widget.csproj");
        var projects = loaded.Solution.Projects.ToArray();
        Assert.True(projects.Length >= 2);

        using var session = new WorkspaceSession(loaded, epoch: 1, compilationLruCapacity: 1);

        var firstA = await session.GetCompilationAsync(projects[0].Id);
        Assert.Equal(1, session.CompilationCache.Count);
        Assert.Equal(0, session.CompilationCache.Evictions);

        _ = await session.GetCompilationAsync(projects[1].Id);
        Assert.Equal(1, session.CompilationCache.Count);
        Assert.Equal(1, session.CompilationCache.Evictions);

        _ = await session.GetCompilationAsync(projects[0].Id);
        Assert.Equal(1, session.CompilationCache.Count);
        Assert.Equal(2, session.CompilationCache.Evictions);
        // Roslyn may return the same Compilation instance after recompile; eviction count is the seam.
    }

    [Fact]
    public async Task session_snapshot_compilations_stay_tied_to_frozen_solution()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var loaded = FakeSolutionLoader.CreateSymbolsLoadedOnDisk(dir);
            using var session = new WorkspaceSession(loaded, epoch: 1);
            var projectId = session.Solution.Projects.Single().Id;
            var before = await session.GetCompilationAsync(projectId);
            var beforeText = before.SyntaxTrees.First(t => t.FilePath?.EndsWith("Calculator.cs") == true)
                .GetText()
                .ToString();

            var calcPath = Path.Combine(dir, "Calculator.cs");
            var updated = beforeText.Replace(
                "public void Reset() { Name = \"calc\"; Mode = 0; }",
                "public void Reset() { Name = \"calc\"; Mode = 0; }\n                public int Extra() => 1;",
                StringComparison.Ordinal);
            Assert.True(loaded.TryUpdateDocumentFromText(calcPath, SourceText.From(updated)));

            var after = await session.GetCompilationAsync(projectId);
            Assert.Same(before, after);
            Assert.DoesNotContain("Extra()", after.SyntaxTrees.First(t => t.FilePath?.EndsWith("Calculator.cs") == true)
                .GetText()
                .ToString(), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task get_generator_run_result_caches_by_project_and_epoch_across_sessions()
    {
        var loaded = FakeSolutionLoader.CreateGeneratorsLoaded();
        var projectId = loaded.Solution.Projects.Single().Id;
        var cache = new GeneratorRunCache();

        using var sessionA = new WorkspaceSession(loaded, epoch: 3, generatorRunCache: cache);
        var first = await sessionA.GetGeneratorRunResultAsync(projectId);

        using var sessionB = new WorkspaceSession(loaded, epoch: 3, generatorRunCache: cache);
        var second = await sessionB.GetGeneratorRunResultAsync(projectId);
        Assert.Same(first, second);

        using var sessionC = new WorkspaceSession(loaded, epoch: 4, generatorRunCache: cache);
        var third = await sessionC.GetGeneratorRunResultAsync(projectId);
        Assert.NotSame(first, third);
    }

    [Fact]
    public async Task without_generated_trees_strips_workspace_generated_documents()
    {
        var loaded = FakeSolutionLoader.CreateGeneratorsLoaded();
        var project = loaded.Solution.Projects.Single();
        using var session = new WorkspaceSession(loaded, epoch: 1);

        var full = await session.GetCompilationAsync(project.Id);
        var stripped = await session.GetCompilationWithoutGeneratedTreesAsync(project.Id);

        var generatedDocs = (await project.GetSourceGeneratedDocumentsAsync()).ToArray();
        Assert.NotEmpty(generatedDocs);
        Assert.True(full.SyntaxTrees.Count() >= stripped.SyntaxTrees.Count());
    }

    [Fact]
    public void workspace_host_options_default_compilation_lru_capacity_is_fifty()
    {
        Assert.Equal(50, WorkspaceHostOptions.Default.CompilationLruCapacity);
        Assert.Equal(50, WorkspaceSession.DefaultCompilationLruCapacity);
    }
}
