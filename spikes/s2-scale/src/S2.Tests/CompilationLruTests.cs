using S2.Core;
using Xunit;
using Xunit.Abstractions;

namespace S2.Tests;

public sealed class CompilationLruTests
{
    private readonly ITestOutputHelper _output;

    public CompilationLruTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Capacity_two_evicts_least_recently_used()
    {
        await using var loaded = await SolutionLoader.OpenSolutionAsync(SpikePaths.SampleSlnx);
        var projects = loaded.Solution.Projects.Take(3).ToArray();
        Assert.True(projects.Length >= 3);

        var lru = new CompilationLru(capacity: 2);
        await lru.GetOrAddAsync(projects[0]);
        await lru.GetOrAddAsync(projects[1]);
        Assert.Equal(2, lru.Count);
        Assert.Equal(0, lru.Evictions);

        await lru.GetOrAddAsync(projects[2]);
        Assert.Equal(2, lru.Count);
        Assert.Equal(1, lru.Evictions);

        // projects[0] was LRU and should be gone; re-add causes another eviction of projects[1]
        await lru.GetOrAddAsync(projects[0]);
        Assert.Equal(2, lru.Evictions);

        _output.WriteLine($"Evictions={lru.Evictions} Count={lru.Count}");
    }
}
