using System.Text.RegularExpressions;
using S2.Core;
using Xunit;
using Xunit.Abstractions;

namespace S2.Tests;

/// <summary>
/// Full-scale smoke against Observables.slnx. Skipped unless OBSERVABLES_SLNX exists
/// and RUN_SCALE_TESTS=1.
/// </summary>
public sealed class ObservablesScaleSmokeTests
{
    private readonly ITestOutputHelper _output;

    public ObservablesScaleSmokeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Scale", "Full")]
    public async Task OpenSolutionAsync_opens_Observables_slnx_when_enabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_SCALE_TESTS"), "1", StringComparison.Ordinal))
        {
            _output.WriteLine("Skipped: set RUN_SCALE_TESTS=1 to enable.");
            return;
        }

        var path = SpikePaths.DefaultObservablesSlnx;
        Assert.True(File.Exists(path), $"Observables.slnx not found: {path}");

        await using var loaded = await SolutionLoader.OpenSolutionAsync(path);
        _output.WriteLine($"Projects={loaded.Solution.ProjectIds.Count}");
        _output.WriteLine($"ElapsedMs={loaded.LoadElapsed.TotalMilliseconds:F0}");
        _output.WriteLine($"PeakWSMiB={loaded.PeakWorkingSetBytes / (1024.0 * 1024):F1}");
        _output.WriteLine($"Diagnostics={loaded.Diagnostics.Count}");

        var multi = loaded.Solution.Projects
            .GroupBy(p => p.FilePath ?? "")
            .Where(g => g.Count() > 1)
            .Take(5)
            .ToArray();
        foreach (var g in multi)
        {
            _output.WriteLine($"MultiTFM {g.Key}: {string.Join(", ", g.Select(p => p.Name))}");
            Assert.All(g, p => Assert.Matches(new Regex(@"\(net", RegexOptions.IgnoreCase), p.Name));
        }

        Assert.True(loaded.Solution.ProjectIds.Count > 50);
    }
}
