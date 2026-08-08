using System.Diagnostics;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class WorkspaceLoadSeamTests
{
    [Fact]
    public async Task workspace_open_returns_immediately_while_load_still_running()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        var delay = TimeSpan.FromMilliseconds(800);
        var loader = FakeSolutionLoader.DelayedMultiTfm(delay);

        try
        {
            await using var fx = new InProcessMcpFixture(TrustedRoots.Create([root]), loader);
            var sw = Stopwatch.StartNew();
            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            sw.Stop();

            Assert.True(open.IsError is not true);
            Assert.True(sw.Elapsed < delay, $"open blocked for {sw.Elapsed}; expected << {delay}");

            var body = InProcessMcpFixture.Deserialize<WorkspaceOpenResultDto>(open);
            Assert.Equal("loading", body.Phase);
            Assert.Contains("workspace_status", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("do not retry workspace_open", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task workspace_status_reaches_ready_after_open()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.DelayedMultiTfm(TimeSpan.FromMilliseconds(150)));

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true);

            WorkspaceStatusDto? status = null;
            for (var i = 0; i < 40; i++)
            {
                var result = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
                Assert.True(result.IsError is not true);
                status = InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(result);
                if (status.Phase == "ready")
                {
                    break;
                }

                await Task.Delay(50);
            }

            Assert.NotNull(status);
            Assert.Equal("ready", status!.Phase);
            Assert.True(status.CompletedUnits >= 1);
            Assert.True(status.TotalUnits >= 1);
            Assert.Contains("Proceed", status.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task workspace_list_projects_errors_with_workspace_not_ready_while_loading()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.DelayedMultiTfm(TimeSpan.FromMilliseconds(1000)));

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true);

            var list = await fx.Client.CallToolAsync(
                "workspace_list_projects",
                new Dictionary<string, object?>());

            Assert.True(list.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(list);
            Assert.Equal(PolicyErrorCodes.WorkspaceNotReady, body.Error);
            Assert.Contains("workspace_status", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("do not retry workspace_open", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task workspace_list_projects_returns_one_row_per_tfm_when_ready()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateMultiTfm());

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true);

            WorkspaceStatusDto? status = null;
            for (var i = 0; i < 40; i++)
            {
                var poll = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
                status = InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(poll);
                if (status.Phase == "ready")
                {
                    break;
                }

                await Task.Delay(25);
            }

            Assert.Equal("ready", status?.Phase);

            var list = await fx.Client.CallToolAsync(
                "workspace_list_projects",
                new Dictionary<string, object?>());
            Assert.True(list.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list);

            Assert.Equal(2, body.Projects.Count);
            Assert.Contains(body.Projects, p => p.Name.Contains("net8.0", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(body.Projects, p => p.Name.Contains("net9.0", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(body.Projects, p => p.TargetFramework == "net8.0");
            Assert.Contains(body.Projects, p => p.TargetFramework == "net9.0");
            Assert.Equal(2, body.Projects.Select(p => p.ProjectId).Distinct().Count());
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
