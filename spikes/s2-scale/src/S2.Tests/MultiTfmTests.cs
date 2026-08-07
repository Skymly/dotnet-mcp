using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.MSBuild;
using S2.Core;
using Xunit;
using Xunit.Abstractions;

namespace S2.Tests;

public sealed class MultiTfmTests
{
    private static readonly Regex TfmSuffix = new(@"\((net[0-9][^)]*)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ITestOutputHelper _output;

    public MultiTfmTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task MultiTfm_csproj_produces_one_ProjectId_per_TFM_with_suffix_in_Name()
    {
        MsBuildBootstrap.EnsureRegistered();
        using var workspace = MSBuildWorkspace.Create();
        _ = await workspace.OpenProjectAsync(SpikePaths.MultiTfmProject);

        var related = workspace.CurrentSolution.Projects
            .Where(p => p.FilePath is not null &&
                        string.Equals(
                            Path.GetFullPath(p.FilePath),
                            Path.GetFullPath(SpikePaths.MultiTfmProject),
                            StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

        _output.WriteLine($"Projects: {string.Join(" | ", related.Select(p => $"{p.Name} [{p.Id.Id}]"))}");

        Assert.True(related.Length >= 2, $"Expected >=2 TFM projects, got {related.Length}");
        Assert.Equal(related.Length, related.Select(p => p.Id).Distinct().Count());
        Assert.All(related, p => Assert.Matches(TfmSuffix, p.Name));
        Assert.Contains(related, p => p.Name.Contains("net8.0", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(related, p => p.Name.Contains("net9.0", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Forcing_single_TargetFramework_property_may_omit_TFM_suffix_in_Name()
    {
        // Document alternate host behavior: OpenProjectAsync with TargetFramework property
        // can yield Name without "(netX.Y)" even though a TFM was selected.
        MsBuildBootstrap.EnsureRegistered();
        using var workspace = MSBuildWorkspace.Create(new Dictionary<string, string>
        {
            ["TargetFramework"] = "net8.0",
        });
        var project = await workspace.OpenProjectAsync(SpikePaths.MultiTfmProject);
        _output.WriteLine($"Forced TFM Name={project.Name} Id={project.Id.Id}");

        // Either form is acceptable; record which we got for CONCLUSIONS.
        Assert.False(string.IsNullOrWhiteSpace(project.Name));
        Assert.Contains("MultiTfm", project.Name, StringComparison.OrdinalIgnoreCase);
    }
}
