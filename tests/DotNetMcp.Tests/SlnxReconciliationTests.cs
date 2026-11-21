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
    public async Task slnx_batch_retains_solution_properties_and_native_project_references()
    {
        var scratch = Directory.CreateTempSubdirectory("dotnet-mcp-slnx-test-");
        try
        {
            var root = PathPolicy.Normalize(scratch.FullName);
            var app = Path.Combine(root, "App.csproj");
            var broken = Path.Combine(root, "Broken.csproj");
            var library = Path.Combine(root, "Library", "Library.csproj");
            var consumer = Path.Combine(root, "Consumer", "Consumer.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(library)!);
            Directory.CreateDirectory(Path.GetDirectoryName(consumer)!);

            var solutionPath = Path.Combine(root, "Nested.slnx");
            await File.WriteAllTextAsync(solutionPath, """
                <Solution>
                  <Project Path="App.csproj" />
                  <Folder Name="/Samples/">
                    <Folder Name="/Samples/Nested/">
                      <Project Path="Library/Library.csproj" />
                      <Project Path="Consumer/Consumer.csproj" />
                    </Folder>
                  </Folder>
                </Solution>
                """);
            const string project = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """;
            await File.WriteAllTextAsync(app, project);
            await File.WriteAllTextAsync(broken, project.Replace("</Project>", """
                  <InvalidMsBuildElement />
                </Project>
                """, StringComparison.Ordinal));
            await File.WriteAllTextAsync(library, project);
            await File.WriteAllTextAsync(consumer, project.Replace("</Project>", """
                  <ItemGroup>
                    <ProjectReference Include="$(SolutionDir)App.csproj" />
                    <ProjectReference Include="../Library/Library.csproj" />
                  </ItemGroup>
                </Project>
                """, StringComparison.Ordinal));

            MsBuildBootstrap.EnsureRegistered();
            using var workspace = new SlnxWorkspace();
            await workspace.OpenSolutionFileAsync(solutionPath, CancellationToken.None);
            Assert.Single(workspace.CurrentSolution.ProjectIds);

            var warnings = new List<string>();
            Assert.False(await workspace.TryOpenBatchAsync([library, broken], warnings, CancellationToken.None));
            Assert.Single(workspace.CurrentSolution.ProjectIds);
            Assert.Empty(warnings);

            Assert.True(await workspace.TryOpenBatchAsync([library, consumer], warnings, CancellationToken.None));

            var projects = workspace.CurrentSolution.Projects.ToDictionary(p => p.Name);
            Assert.Equal(3, projects.Count);
            Assert.Equal(solutionPath, workspace.CurrentSolution.FilePath);
            Assert.Equal(
                new[] { projects["App"].Id, projects["Library"].Id }.OrderBy(id => id.Id),
                projects["Consumer"].ProjectReferences.Select(reference => reference.ProjectId).OrderBy(id => id.Id));
            Assert.All(projects.Values, p => Assert.True(PathPolicy.IsUnderRoot(p.FilePath!, root)));
            Assert.DoesNotContain(warnings, warning =>
                warning.Contains("Traversal.csproj", StringComparison.Ordinal));

            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(library)!, "global.json"), "{}");
            Assert.Null(SlnxProjectBatch.TryPlan([library, consumer]));

            File.Delete(Path.Combine(Path.GetDirectoryName(library)!, "global.json"));
            await File.WriteAllTextAsync(Path.Combine(root, "global.json"), """{"sdk":{"paths":["."]}}""");
            Assert.Null(SlnxProjectBatch.TryPlan([library, consumer]));
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

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
            using var workspace = new AdhocWorkspace();
            workspace.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Create(),
                "App",
                "App",
                LanguageNames.CSharp,
                filePath: projectPath));

            var loadedPaths = new MsBuildSolutionLoader.LoadedProjectPaths();
            loadedPaths.AddFrom(workspace.CurrentSolution);

            Assert.True(loadedPaths.ContainsCanonical(SlnxProjectEnumerator.Canonicalize(projectPath)));
            // Same file through a lexical detour still counts as loaded.
            Assert.True(loadedPaths.ContainsCanonical(SlnxProjectEnumerator.Canonicalize(
                Path.Combine(dir, "sub", "..", "App.csproj"))));
            Assert.False(loadedPaths.ContainsCanonical(SlnxProjectEnumerator.Canonicalize(
                Path.Combine(dir, "Other.csproj"))));
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
