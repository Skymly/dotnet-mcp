using DotNetMcp.Server;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotNetMcp.Tests;

public class ProjectListGeneratorsSeamTests
{
    [Fact]
    public async Task project_list_generators_errors_with_workspace_not_ready_while_loading()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.DelayedWithGenerators(TimeSpan.FromMilliseconds(1000)));

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true);

            var result = await fx.Client.CallToolAsync(
                "project_list_generators",
                new Dictionary<string, object?> { ["projectId"] = Guid.NewGuid().ToString("D") });

            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.WorkspaceNotReady, body.Error);
            Assert.Contains("workspace_status", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task project_list_generators_rejects_unknown_project_id()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithGenerators());

            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var result = await fx.Client.CallToolAsync(
                "project_list_generators",
                new Dictionary<string, object?> { ["projectId"] = Guid.NewGuid().ToString("D") });

            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.ProjectNotFound, body.Error);
            Assert.Contains("workspace_list_projects", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task project_list_generators_existing_fsharp_project_is_language_not_supported()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithFsharpAndCSharp());
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var listed = await fx.Client.CallToolAsync(
                "workspace_list_projects",
                new Dictionary<string, object?>());
            Assert.True(listed.IsError is not true, InProcessMcpFixture.TextOf(listed));
            var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(listed).Projects;
            var fsharp = Assert.Single(projects, p => p.Name.Contains("FsLib", StringComparison.OrdinalIgnoreCase));

            var result = await fx.Client.CallToolAsync(
                "project_list_generators",
                new Dictionary<string, object?> { ["projectId"] = fsharp.ProjectId });
            Assert.True(result.IsError is true, InProcessMcpFixture.TextOf(result));
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.GeneratorLanguageNotSupported, body.Error);
            Assert.NotEqual(PolicyErrorCodes.ProjectNotFound, body.Error);
            Assert.DoesNotContain("ready workspace", body.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(body.SuggestedAction));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task project_list_generators_returns_custom_generator_identity()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithGenerators());

            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var list = await fx.Client.CallToolAsync(
                "workspace_list_projects",
                new Dictionary<string, object?>());
            Assert.True(list.IsError is not true);
            var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list);
            var projectId = Assert.Single(projects.Projects).ProjectId;

            var result = await fx.Client.CallToolAsync(
                "project_list_generators",
                new Dictionary<string, object?> { ["projectId"] = projectId });

            Assert.True(result.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<ProjectListGeneratorsResultDto>(result);
            Assert.True(body.Epoch > 0);

            var marker = Assert.Single(
                body.Generators,
                g => g.TypeFullName == "CustomGenerator.MarkerGenerator");
            Assert.Equal("CustomGenerator", marker.AssemblyName);
            Assert.Equal("1.2.3.0", marker.Version);
            Assert.Null(body.UnresolvedAnalyzerReferences);
            Assert.Null(body.Message);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task project_list_generators_reports_declared_but_unresolved_analyzer_paths()
    {
        // Issue #551: an analyzer path MSBuild declared but Roslyn could not materialize an
        // assembly for — the OutputItemType="Analyzer" reference shape with no built output —
        // must not collapse to a silent empty list. The path is reported, identities are not
        // fabricated, and loaded references still list real generators.
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        var missingAnalyzer = Path.Combine(root, "gen", "bin", "Debug", "netstandard2.0", "NotBuilt.dll");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                new FakeSolutionLoader(TimeSpan.Zero, () =>
                {
                    var loaded = FakeSolutionLoader.CreateGeneratorsLoaded(
                        Path.Combine(root, "GeneratorHost.csproj"));
                    var ws = loaded.Workspace;
                    var projectId = ws.CurrentSolution.ProjectIds.Single();
                    if (!ws.TryApplyChanges(ws.CurrentSolution.AddAnalyzerReference(
                            projectId, new UnresolvedAnalyzerReference(missingAnalyzer))))
                    {
                        throw new InvalidOperationException(
                            "Failed to apply the unresolved analyzer reference fixture.");
                    }

                    return new LoadedSolution(ws, ws.CurrentSolution, warnings: []);
                }));

            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var list = await fx.Client.CallToolAsync(
                "workspace_list_projects",
                new Dictionary<string, object?>());
            Assert.True(list.IsError is not true);
            var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list);
            var projectId = Assert.Single(projects.Projects).ProjectId;

            var result = await fx.Client.CallToolAsync(
                "project_list_generators",
                new Dictionary<string, object?> { ["projectId"] = projectId });

            Assert.True(result.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<ProjectListGeneratorsResultDto>(result);

            Assert.Contains(body.Generators, g => g.TypeFullName == "CustomGenerator.MarkerGenerator");
            var unresolved = Assert.Single(body.UnresolvedAnalyzerReferences!);
            Assert.Equal(missingAnalyzer, unresolved);
            Assert.False(string.IsNullOrWhiteSpace(body.Message));
            Assert.Contains("unresolvedAnalyzerReferences", body.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnet-mcp-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
