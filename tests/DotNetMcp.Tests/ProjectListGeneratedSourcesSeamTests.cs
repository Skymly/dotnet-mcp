using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class ProjectListGeneratedSourcesSeamTests
{
    [Fact]
    public async Task project_list_generated_sources_returns_marker_hint_and_content()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithGenerators());

            await OpenUntilReadyAsync(fx, solution);
            var projectId = await GetSingleProjectIdAsync(fx);

            var result = await fx.Client.CallToolAsync(
                "project_list_generated_sources",
                new Dictionary<string, object?>
                {
                    ["projectId"] = projectId,
                    ["assemblyName"] = "CustomGenerator",
                    ["typeFullName"] = "CustomGenerator.MarkerGenerator"
                });

            Assert.True(result.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<ProjectListGeneratedSourcesResultDto>(result);
            Assert.True(body.Epoch > 0);
            var item = Assert.Single(body.Items);
            Assert.Equal(CustomGenerator.MarkerGenerator.HintName, item.HintName);
            Assert.Contains("CustomGenerator.MarkerGenerator", item.Content, StringComparison.Ordinal);
            Assert.False(body.Truncated);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task project_list_generated_sources_keeps_collision_hintnames_apart_by_identity()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithGenerators());

            await OpenUntilReadyAsync(fx, solution);
            var projectId = await GetSingleProjectIdAsync(fx);

            var fromA = await fx.Client.CallToolAsync(
                "project_list_generated_sources",
                new Dictionary<string, object?>
                {
                    ["projectId"] = projectId,
                    ["assemblyName"] = "CustomGenerator",
                    ["typeFullName"] = "CustomGenerator.CollisionGeneratorA"
                });
            var fromB = await fx.Client.CallToolAsync(
                "project_list_generated_sources",
                new Dictionary<string, object?>
                {
                    ["projectId"] = projectId,
                    ["assemblyName"] = "CustomGenerator",
                    ["typeFullName"] = "CustomGenerator.CollisionGeneratorB"
                });

            Assert.True(fromA.IsError is not true);
            Assert.True(fromB.IsError is not true);
            var a = InProcessMcpFixture.Deserialize<ProjectListGeneratedSourcesResultDto>(fromA);
            var b = InProcessMcpFixture.Deserialize<ProjectListGeneratedSourcesResultDto>(fromB);

            var itemA = Assert.Single(a.Items);
            var itemB = Assert.Single(b.Items);
            Assert.Equal(CustomGenerator.CollisionGeneratorA.SharedHintName, itemA.HintName);
            Assert.Equal(itemA.HintName, itemB.HintName);
            Assert.Contains("Tag => \"A\"", itemA.Content, StringComparison.Ordinal);
            Assert.Contains("Tag => \"B\"", itemB.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("Tag => \"B\"", itemA.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("Tag => \"A\"", itemB.Content, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task project_list_generated_sources_rejects_unknown_generator()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithGenerators());

            await OpenUntilReadyAsync(fx, solution);
            var projectId = await GetSingleProjectIdAsync(fx);

            var result = await fx.Client.CallToolAsync(
                "project_list_generated_sources",
                new Dictionary<string, object?>
                {
                    ["projectId"] = projectId,
                    ["assemblyName"] = "Missing.Assembly",
                    ["typeFullName"] = "Missing.Type"
                });

            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.GeneratorNotFound, body.Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task project_list_generated_sources_stale_epoch_cursor_errors()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithGenerators());

            await OpenUntilReadyAsync(fx, solution);
            var projectId = await GetSingleProjectIdAsync(fx);

            var stale = DotNetMcp.Core.GeneratedSourcesPageCursor.Encode(
                epoch: 999_999,
                assemblyName: "CustomGenerator",
                typeFullName: "CustomGenerator.MarkerGenerator",
                offset: 0);

            var result = await fx.Client.CallToolAsync(
                "project_list_generated_sources",
                new Dictionary<string, object?>
                {
                    ["projectId"] = projectId,
                    ["assemblyName"] = "CustomGenerator",
                    ["typeFullName"] = "CustomGenerator.MarkerGenerator",
                    ["cursor"] = stale
                });

            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.StaleCursor, body.Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<string> GetSingleProjectIdAsync(InProcessMcpFixture fx)
    {
        var list = await fx.Client.CallToolAsync(
            "workspace_list_projects",
            new Dictionary<string, object?>());
        Assert.True(list.IsError is not true);
        var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list);
        return Assert.Single(projects.Projects).ProjectId;
    }

    private static async Task OpenUntilReadyAsync(InProcessMcpFixture fx, string solution)
    {
        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = solution });
        Assert.True(open.IsError is not true);

        for (var i = 0; i < 40; i++)
        {
            var poll = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
            var status = InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(poll);
            if (status.Phase == "ready")
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("workspace did not become ready");
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
