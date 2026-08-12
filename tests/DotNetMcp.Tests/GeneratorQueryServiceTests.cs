using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class GeneratorQueryServiceTests
{
    [Fact]
    public async Task list_generators_caches_by_project_id_and_epoch()
    {
        var loaded = FakeSolutionLoader.CreateGeneratorsLoaded();
        var projectId = loaded.Solution.Projects.Single().Id.Id.ToString("D");
        var service = new GeneratorQueryService();

        using var session7 = new WorkspaceSession(loaded, epoch: 7);
        var (first, error1) = await service.ListGeneratorsAsync(session7, projectId);
        Assert.Null(error1);
        Assert.NotNull(first);

        using var session7b = new WorkspaceSession(loaded, epoch: 7);
        var (second, error2) = await service.ListGeneratorsAsync(session7b, projectId);
        Assert.Null(error2);
        Assert.Same(first, second);

        using var session8 = new WorkspaceSession(loaded, epoch: 8);
        var (third, error3) = await service.ListGeneratorsAsync(session8, projectId);
        Assert.Null(error3);
        Assert.NotSame(first, third);
        Assert.Equal(first!.Count, third!.Count);
    }

    [Fact]
    public async Task list_generated_sources_and_driver_cache_share_epoch_key()
    {
        var loaded = FakeSolutionLoader.CreateGeneratorsLoaded();
        var projectId = loaded.Solution.Projects.Single().Id.Id.ToString("D");
        var service = new GeneratorQueryService();
        var cache = new GeneratorRunCache();

        using var session3 = new WorkspaceSession(loaded, epoch: 3, generatorRunCache: cache);
        var (page1, error1) = await service.ListGeneratedSourcesAsync(
            session3,
            projectId,
            assemblyName: "CustomGenerator",
            typeFullName: "CustomGenerator.MarkerGenerator");
        Assert.Null(error1);
        Assert.NotNull(page1);
        Assert.Contains(page1!.Items, i => i.HintName == CustomGenerator.MarkerGenerator.HintName);

        var (snap1, snapError1) = await service.GetDriverRunAsync(session3, projectId);
        Assert.Null(snapError1);
        Assert.NotNull(snap1);

        using var session3b = new WorkspaceSession(loaded, epoch: 3, generatorRunCache: cache);
        var (snap2, snapError2) = await service.GetDriverRunAsync(session3b, projectId);
        Assert.Null(snapError2);
        Assert.Same(snap1, snap2);

        using var session4 = new WorkspaceSession(loaded, epoch: 4, generatorRunCache: cache);
        var (snap3, snapError3) = await service.GetDriverRunAsync(session4, projectId);
        Assert.Null(snapError3);
        Assert.NotSame(snap1, snap3);
    }

    [Fact]
    public async Task list_generator_diagnostics_returns_fixture_diagnostic()
    {
        var loaded = FakeSolutionLoader.CreateGeneratorsLoaded();
        var projectId = loaded.Solution.Projects.Single().Id.Id.ToString("D");
        var service = new GeneratorQueryService();

        using var session = new WorkspaceSession(loaded, epoch: 1);
        var (page, error) = await service.ListGeneratorDiagnosticsAsync(
            session,
            projectId,
            assemblyName: "CustomGenerator",
            typeFullName: "CustomGenerator.DiagnosticEmittingGenerator");

        Assert.Null(error);
        Assert.NotNull(page);
        Assert.Equal("CustomGenerator", page!.Identity.AssemblyName);
        Assert.Equal("CustomGenerator.DiagnosticEmittingGenerator", page.Identity.TypeFullName);
        var item = Assert.Single(page.Page.Items);
        Assert.Equal(CustomGenerator.DiagnosticEmittingGenerator.DiagnosticId, item.Id);
        Assert.Equal(nameof(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning), item.Severity);
        Assert.Equal(CustomGenerator.DiagnosticEmittingGenerator.DiagnosticMessage, item.Message);
        Assert.False(page.Page.Truncated);
    }
}
