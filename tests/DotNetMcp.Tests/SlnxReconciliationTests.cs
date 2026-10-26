using DotNetMcp.Server;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Tests;

/// <summary>
/// Issue #545: MSBuildWorkspace's slnx handling silently dropped projects declared inside
/// nested (depth-2+) solution folders. The loader reconciles the declared &lt;Project Path&gt;
/// set against what actually loaded, retries the missing ones on the same workspace, and
/// names any that still refuse to load in the load warnings.
/// </summary>
public class SlnxReconciliationTests
{
    private static string FixtureDir => Path.Combine(
        MsBuildWorkspaceIntegrationTests.FixturesRoot, "NestedFolders");

    private static string NestedSlnx => Path.Combine(FixtureDir, "Nested.slnx");
    private static string UnloadableSlnx => Path.Combine(FixtureDir, "Unloadable.slnx");

    [Fact]
    public async Task slnx_nested_folder_projects_at_any_depth_all_load()
    {
        Assert.True(File.Exists(NestedSlnx), $"Missing fixture: {NestedSlnx}");

        await using var loaded = await new MsBuildSolutionLoader(
            TrustedRoots.Create([FixtureDir])).OpenAsync(NestedSlnx);

        var projectPaths = loaded.Solution.Projects.Select(p => p.FilePath).ToArray();
        Assert.Contains(projectPaths, p =>
            p is not null && p.EndsWith("Root.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(projectPaths, p =>
            p is not null && p.EndsWith("Outer.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(projectPaths, p =>
            p is not null && p.EndsWith("Inner.csproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task slnx_declared_but_unloadable_projects_warn_and_do_not_abort_the_rest()
    {
        Assert.True(File.Exists(UnloadableSlnx), $"Missing fixture: {UnloadableSlnx}");

        await using var loaded = await new MsBuildSolutionLoader(
            TrustedRoots.Create([FixtureDir])).OpenAsync(UnloadableSlnx);

        // A missing file and an unsupported shared project keep the named warnings;
        // the good projects — including the depth-2 one retried after Gone failed — load.
        var projectPaths = loaded.Solution.Projects.Select(p => p.FilePath).ToArray();
        Assert.Contains(projectPaths, p =>
            p is not null && p.EndsWith("Root.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(projectPaths, p =>
            p is not null && p.EndsWith("Inner.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(projectPaths, p =>
            p is not null && p.EndsWith("Gone.csproj", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(loaded.Warnings, w =>
            w.Contains("Project declared in solution but not loaded", StringComparison.Ordinal)
            && w.Contains("Gone.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(loaded.Warnings, w =>
            w.Contains("Project declared in solution but not loaded", StringComparison.Ordinal)
            && w.Contains("Shared.shproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void slnx_project_enumerator_finds_project_paths_at_any_folder_depth()
    {
        Assert.True(File.Exists(NestedSlnx), $"Missing fixture: {NestedSlnx}");

        var declared = SlnxProjectEnumerator.EnumerateProjectPaths(NestedSlnx);

        Assert.Equal(3, declared.Count);
        Assert.Contains(declared, p => p.EndsWith("Root.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(declared, p => p.EndsWith("Outer.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(declared, p => p.EndsWith("Inner.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.All(declared, p => Assert.True(Path.IsPathFullyQualified(p)));
    }

    [Fact]
    public void is_project_already_loaded_matches_canonical_file_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dotnet-mcp-dedup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        try
        {
            var projectPath = Path.Combine(dir, "App.csproj");
            var workspace = new AdhocWorkspace();
            workspace.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Create(),
                "App",
                "App",
                LanguageNames.CSharp,
                filePath: projectPath));

            Assert.True(MsBuildSolutionLoader.IsProjectAlreadyLoaded(
                workspace.CurrentSolution, projectPath));
            // Same file through a lexical detour still counts as loaded.
            Assert.True(MsBuildSolutionLoader.IsProjectAlreadyLoaded(
                workspace.CurrentSolution,
                Path.Combine(dir, "sub", "..", "App.csproj")));
            Assert.False(MsBuildSolutionLoader.IsProjectAlreadyLoaded(
                workspace.CurrentSolution,
                Path.Combine(dir, "Other.csproj")));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
