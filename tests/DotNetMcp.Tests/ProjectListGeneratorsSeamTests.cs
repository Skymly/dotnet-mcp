using DotNetMcp.Server;

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
