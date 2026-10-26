using DotNetMcp.Server;

namespace DotNetMcp.Tests;

/// <summary>
/// Issue #547: an .slnf entry whose project is already in the workspace — a duplicate
/// filter line, or a project an earlier entry pulled in transitively — used to hit
/// MSBuildWorkspace's "already part of the workspace" and abort the whole open. Each
/// entry is now opened via <see cref="MsBuildSolutionLoader.OpenProjectIfMissingAsync"/>
/// and a skip is named in the load warnings, never silent.
/// </summary>
public class SlnfDedupTests
{
    private static string FixtureDir => Path.Combine(
        MsBuildWorkspaceIntegrationTests.FixturesRoot, "SampleFilter");

    private static string DuplicateSlnf => Path.Combine(FixtureDir, "SampleDuplicate.slnf");
    private static string TransitiveSlnf => Path.Combine(FixtureDir, "SampleTransitive.slnf");
    private static string MissingSlnf => Path.Combine(FixtureDir, "SampleMissing.slnf");
    private static string MultiTfmSlnf => Path.Combine(FixtureDir, "SampleMultiTfm.slnf");

    private const string DedupWarning = "Solution filter entry already loaded transitively";

    [Fact]
    public async Task slnf_duplicate_project_entry_is_skipped_with_named_warning()
    {
        Assert.True(File.Exists(DuplicateSlnf), $"Missing fixture: {DuplicateSlnf}");

        await using var loaded = await new MsBuildSolutionLoader(
            TrustedRoots.Create([FixtureDir])).OpenAsync(DuplicateSlnf);

        Assert.Contains(loaded.Solution.Projects, p =>
            p.FilePath?.EndsWith("LibA.csproj", StringComparison.OrdinalIgnoreCase) == true);

        var warning = Assert.Single(loaded.Warnings,
            w => w.Contains(DedupWarning, StringComparison.Ordinal));
        Assert.Contains("LibA.csproj", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task slnf_entry_already_loaded_transitively_is_skipped_with_named_warning()
    {
        Assert.True(File.Exists(TransitiveSlnf), $"Missing fixture: {TransitiveSlnf}");

        // App is listed first; its ProjectReference graph pulls LibA and LibB into the
        // workspace before LibA's own filter entry runs, so that entry dedups away.
        await using var loaded = await new MsBuildSolutionLoader(
            TrustedRoots.Create([FixtureDir])).OpenAsync(TransitiveSlnf);

        var projectPaths = loaded.Solution.Projects.Select(p => p.FilePath).ToArray();
        Assert.Contains(projectPaths, p =>
            p is not null && p.EndsWith("App.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(projectPaths, p =>
            p is not null && p.EndsWith("LibA.csproj", StringComparison.OrdinalIgnoreCase));

        var warning = Assert.Single(loaded.Warnings,
            w => w.Contains(DedupWarning, StringComparison.Ordinal));
        Assert.Contains("LibA.csproj", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task slnf_project_referenced_but_not_listed_still_loads_transitively()
    {
        // The filter lists LibA + App but not LibB; a filter picks the entry points and
        // the ProjectReference closure comes along — LibB must still be in the workspace.
        await using var loaded = await new MsBuildSolutionLoader(
            TrustedRoots.Create([FixtureDir])).OpenAsync(
                MsBuildWorkspaceIntegrationTests.SampleSlnf);

        Assert.Contains(loaded.Solution.Projects, p =>
            p.FilePath?.EndsWith("LibB.csproj", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task slnf_missing_project_file_fails_the_open_like_before()
    {
        Assert.True(File.Exists(MissingSlnf), $"Missing fixture: {MissingSlnf}");

        // Decision: a listed path that does not exist is NOT a dedup skip — it is a
        // stale filter, so the error propagates and the whole open keeps failing.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            new MsBuildSolutionLoader(TrustedRoots.Create([FixtureDir])).OpenAsync(MissingSlnf));
        Assert.Contains("Ghost", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task slnf_duplicate_multi_tfm_entry_loads_each_tfm_once()
    {
        Assert.True(File.Exists(MultiTfmSlnf), $"Missing fixture: {MultiTfmSlnf}");

        // MultiTfm.csproj (net8.0 + net9.0) lives in the sibling MultiTfm fixture dir,
        // so the trusted root covers the whole fixtures tree.
        await using var loaded = await new MsBuildSolutionLoader(
            TrustedRoots.Create([MsBuildWorkspaceIntegrationTests.FixturesRoot])).OpenAsync(MultiTfmSlnf);

        var names = loaded.Solution.Projects
            .Where(p => p.FilePath?.EndsWith("MultiTfm.csproj", StringComparison.OrdinalIgnoreCase) == true)
            .Select(p => p.Name)
            .ToArray();
        Assert.Equal(2, names.Length);
        Assert.Contains(names, n => n.Contains("net8.0", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("net9.0", StringComparison.Ordinal));

        var warning = Assert.Single(loaded.Warnings,
            w => w.Contains(DedupWarning, StringComparison.Ordinal));
        Assert.Contains("MultiTfm.csproj", warning, StringComparison.OrdinalIgnoreCase);
    }
}
