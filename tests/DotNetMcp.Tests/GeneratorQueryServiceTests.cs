using DotNetMcp.Core;

namespace DotNetMcp.Tests;

public class GeneratorQueryServiceTests
{
    [Fact]
    public async Task list_generators_caches_by_project_id_and_epoch()
    {
        var loaded = FakeSolutionLoader.CreateGeneratorsLoaded();
        var projectId = loaded.Solution.Projects.Single().Id.Id.ToString("D");
        var service = new GeneratorQueryService();

        var (first, error1) = await service.ListGeneratorsAsync(loaded.Solution, projectId, epoch: 7);
        Assert.Null(error1);
        Assert.NotNull(first);

        var (second, error2) = await service.ListGeneratorsAsync(loaded.Solution, projectId, epoch: 7);
        Assert.Null(error2);
        Assert.Same(first, second);

        var (third, error3) = await service.ListGeneratorsAsync(loaded.Solution, projectId, epoch: 8);
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

        var (page1, error1) = await service.ListGeneratedSourcesAsync(
            loaded.Solution,
            projectId,
            epoch: 3,
            assemblyName: "CustomGenerator",
            typeFullName: "CustomGenerator.MarkerGenerator");
        Assert.Null(error1);
        Assert.NotNull(page1);
        Assert.Contains(page1!.Items, i => i.HintName == CustomGenerator.MarkerGenerator.HintName);

        var (snap1, snapError1) = await service.GetDriverRunAsync(loaded.Solution, projectId, epoch: 3);
        Assert.Null(snapError1);
        Assert.NotNull(snap1);

        var (snap2, snapError2) = await service.GetDriverRunAsync(loaded.Solution, projectId, epoch: 3);
        Assert.Null(snapError2);
        Assert.Same(snap1, snap2);

        var (snap3, snapError3) = await service.GetDriverRunAsync(loaded.Solution, projectId, epoch: 4);
        Assert.Null(snapError3);
        Assert.NotSame(snap1, snap3);
    }
}
