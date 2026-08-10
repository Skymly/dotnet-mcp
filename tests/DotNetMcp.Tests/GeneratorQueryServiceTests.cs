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
}
