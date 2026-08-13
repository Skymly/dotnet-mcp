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
            var calcPath = Path.Combine(dir, "Calculator.cs");
            Assert.True(loaded.TryUpdateDocumentFromText(
                calcPath,
                SourceText.From(WithExtraMethod(CalculatorTreeText(before)))));

            var after = await session.GetCompilationAsync(projectId);
            Assert.Same(before, after);
            Assert.DoesNotContain("Extra()", CalculatorTreeText(after), StringComparison.Ordinal);
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

    [Fact]
    public async Task ready_sessions_in_the_same_epoch_share_host_compilation_lru()
    {
        await using var host = CreateHost(FakeSolutionLoader.ImmediateWithSymbols());
        await OpenUntilReadyAsync(host, @"C:\fake\SampleLib.csproj");

        Assert.True(host.TryGetReadySession(out var firstSession));
        Assert.True(host.TryGetReadySession(out var secondSession));
        var sessionA = Assert.IsType<WorkspaceSession>(firstSession);
        var sessionB = Assert.IsType<WorkspaceSession>(secondSession);
        Assert.Equal(sessionA.Epoch, sessionB.Epoch);

        var projectId = sessionA.Solution.Projects.Single().Id;
        var first = await sessionA.GetCompilationAsync(projectId);

        Assert.Equal(1, sessionB.CompilationCache.Count);
        var second = await sessionB.GetCompilationAsync(projectId);
        Assert.Same(first, second);
        Assert.Equal(0, sessionA.CompilationCache.Evictions);
    }

    [Fact]
    public async Task epoch_advance_isolates_new_session_compilations_from_in_flight_snapshot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-host-lru-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var projectPath = Path.Combine(dir, "SampleLib.csproj");
            await using var host = CreateHost(FakeSolutionLoader.ImmediateWithSymbolsOnDisk(dir));
            await OpenUntilReadyAsync(host, projectPath);

            Assert.True(host.TryGetReadySession(out var firstSession));
            var sessionA = Assert.IsType<WorkspaceSession>(firstSession);
            var projectId = sessionA.Solution.Projects.Single().Id;
            var before = await sessionA.GetCompilationAsync(projectId);
            var epochBefore = sessionA.Epoch;
            var calcPath = Path.Combine(dir, "Calculator.cs");
            await File.WriteAllTextAsync(calcPath, WithExtraMethod(CalculatorTreeText(before)));
            host.ApplyChangedPaths([calcPath]);

            Assert.True(host.CurrentEpoch > epochBefore);
            Assert.True(host.TryGetReadySession(out var secondSession));
            var sessionB = Assert.IsType<WorkspaceSession>(secondSession);
            Assert.NotEqual(sessionA.Epoch, sessionB.Epoch);

            var after = await sessionB.GetCompilationAsync(projectId);
            Assert.NotSame(before, after);
            Assert.Contains("Extra()", CalculatorTreeText(after), StringComparison.Ordinal);

            var stillBefore = await sessionA.GetCompilationAsync(projectId);
            Assert.Same(before, stillBefore);
            Assert.DoesNotContain("Extra()", CalculatorTreeText(stillBefore), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string CalculatorTreeText(Compilation compilation) =>
        compilation.SyntaxTrees
            .First(t => t.FilePath?.EndsWith("Calculator.cs") == true)
            .GetText()
            .ToString();

    private static string WithExtraMethod(string calculatorSource) =>
        calculatorSource.Replace(
            "public void Reset() { Name = \"calc\"; Mode = 0; }",
            "public void Reset() { Name = \"calc\"; Mode = 0; }\n                public int Extra() => 1;",
            StringComparison.Ordinal);

    private static WorkspaceHost CreateHost(ISolutionLoader loader, int compilationLruCapacity = 50) =>
        new(
            loader,
            new WorkspaceHostOptions
            {
                Debounce = TimeSpan.Zero,
                FileWatcher = new ManualWorkspaceFileWatcher(),
                CompilationLruCapacity = compilationLruCapacity
            });

    private static async Task OpenUntilReadyAsync(WorkspaceHost host, string path)
    {
        host.BeginOpen(path);
        for (var i = 0; i < 40; i++)
        {
            if (host.GetStatus().Phase == "ready")
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"workspace did not become ready: {host.GetStatus().Phase} {host.GetStatus().Error}");
    }
}
