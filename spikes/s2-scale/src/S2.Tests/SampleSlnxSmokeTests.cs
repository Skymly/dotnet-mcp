using S2.Core;
using Xunit;
using Xunit.Abstractions;

namespace S2.Tests;

public sealed class SampleSlnxSmokeTests
{
    private readonly ITestOutputHelper _output;

    public SampleSlnxSmokeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task OpenSolutionAsync_opens_fixture_slnx()
    {
        await using var loaded = await SolutionLoader.OpenSolutionAsync(SpikePaths.SampleSlnx);

        _output.WriteLine($"Projects={loaded.Solution.ProjectIds.Count} ElapsedMs={loaded.LoadElapsed.TotalMilliseconds:F0}");
        foreach (var d in loaded.Diagnostics.Take(10))
        {
            _output.WriteLine($"[{d.Kind}] {d.Message}");
        }

        Assert.True(loaded.Solution.ProjectIds.Count >= 3, $"Expected >=3 projects, got {loaded.Solution.ProjectIds.Count}");
    }
}
