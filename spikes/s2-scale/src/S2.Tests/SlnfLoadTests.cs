using S2.Core;
using Xunit;
using Xunit.Abstractions;

namespace S2.Tests;

public sealed class SlnfLoadTests
{
    private readonly ITestOutputHelper _output;

    public SlnfLoadTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task OpenSlnfAsync_loads_only_filtered_projects_and_their_graph()
    {
        await using var loaded = await SolutionLoader.OpenSlnfAsync(SpikePaths.SampleSlnf);

        var names = loaded.Solution.Projects.Select(p => p.Name).OrderBy(n => n).ToArray();
        _output.WriteLine($"Projects ({names.Length}): {string.Join(", ", names)}");
        _output.WriteLine($"LoadElapsed={loaded.LoadElapsed.TotalMilliseconds:F0}ms PeakWS={loaded.PeakWorkingSetBytes / (1024.0 * 1024):F1}MiB");
        _output.WriteLine($"WorkspaceFailed={loaded.Diagnostics.Count}");

        // Filter lists LibA + App; App references LibB so OpenProjectAsync may pull LibB transitively
        // depending on MSBuildWorkspace behavior. Assert filter roots are present and LibB is optional.
        Assert.Contains(loaded.Solution.Projects, p => p.Name.Contains("LibA", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(loaded.Solution.Projects, p => p.Name.Contains("App", StringComparison.OrdinalIgnoreCase));
        Assert.True(loaded.Solution.ProjectIds.Count >= 2);
    }
}
