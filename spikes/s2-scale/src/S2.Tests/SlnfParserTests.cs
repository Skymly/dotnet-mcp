using S2.Core;
using Xunit;

namespace S2.Tests;

public sealed class SlnfParserTests
{
    [Fact]
    public void ParseFile_reads_solution_path_and_filtered_projects()
    {
        var doc = SlnfParser.ParseFile(SpikePaths.SampleSlnf);

        Assert.Equal("Sample.slnx", doc.Solution!.Path);
        Assert.Equal(2, doc.Solution.Projects!.Count);
        Assert.Contains("LibA/LibA.csproj", doc.Solution.Projects);
        Assert.Contains("App/App.csproj", doc.Solution.Projects);
        Assert.DoesNotContain(doc.Solution.Projects, p => p.Contains("LibB", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveProjectPaths_returns_absolute_existing_paths_for_filter_only()
    {
        var paths = SlnfParser.ResolveProjectPaths(SpikePaths.SampleSlnf);

        Assert.Equal(2, paths.Count);
        Assert.All(paths, p => Assert.True(File.Exists(p), $"Missing: {p}"));
        Assert.Contains(paths, p => p.EndsWith("LibA.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, p => p.EndsWith("App.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, p => p.EndsWith("LibB.csproj", StringComparison.OrdinalIgnoreCase));
    }
}
