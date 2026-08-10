using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class ProjectDiagnosticsSeamTests
{
    [Fact]
    public async Task project_diagnostics_errors_with_workspace_not_ready_while_loading()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.DelayedWithDiagnostics(TimeSpan.FromMilliseconds(1000)));

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true);

            var result = await fx.Client.CallToolAsync(
                "project_diagnostics",
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
    public async Task project_diagnostics_rejects_unknown_project_id()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithDiagnostics());

            await OpenUntilReadyAsync(fx, solution);

            var result = await fx.Client.CallToolAsync(
                "project_diagnostics",
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
    public async Task project_diagnostics_pages_with_epoch_cursor_and_continues()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithDiagnostics());

            await OpenUntilReadyAsync(fx, solution);

            var list = await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>());
            var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list);
            var projectId = Assert.Single(projects.Projects).ProjectId;

            var page1 = await fx.Client.CallToolAsync(
                "project_diagnostics",
                new Dictionary<string, object?>
                {
                    ["projectId"] = projectId,
                    ["limit"] = 2
                });

            Assert.True(page1.IsError is not true);
            var first = InProcessMcpFixture.Deserialize<ProjectDiagnosticsResultDto>(page1);
            Assert.Equal(2, first.Items.Count);
            Assert.True(first.Truncated);
            Assert.False(string.IsNullOrWhiteSpace(first.NextCursor));
            Assert.Contains("nextCursor", first.Message, StringComparison.OrdinalIgnoreCase);
            Assert.All(first.Items, d =>
            {
                Assert.False(string.IsNullOrWhiteSpace(d.Id));
                Assert.True(
                    d.Severity is "Error" or "Warning",
                    $"Unexpected severity: {d.Severity}");
                Assert.Equal(projectId, d.ProjectId);
            });

            var all = first.Items.ToList();
            var cursor = first.NextCursor;
            for (var guard = 0; guard < 20 && cursor is not null; guard++)
            {
                var page = await fx.Client.CallToolAsync(
                    "project_diagnostics",
                    new Dictionary<string, object?>
                    {
                        ["projectId"] = projectId,
                        ["limit"] = 2,
                        ["cursor"] = cursor
                    });

                Assert.True(page.IsError is not true);
                var body = InProcessMcpFixture.Deserialize<ProjectDiagnosticsResultDto>(page);
                Assert.NotEmpty(body.Items);
                Assert.DoesNotContain(
                    body.Items,
                    d => all.Any(f =>
                        f.Id == d.Id &&
                        f.Message == d.Message &&
                        f.StartLine == d.StartLine &&
                        f.StartCharacter == d.StartCharacter));
                all.AddRange(body.Items);
                cursor = body.NextCursor;
                if (!body.Truncated)
                {
                    Assert.Null(body.NextCursor);
                    break;
                }
            }

            Assert.Null(cursor);
            Assert.True(all.Count > 2);
            Assert.Contains(all, d => d.Severity == "Warning" && d.Message.Contains("DiagWarnA", StringComparison.Ordinal));
            Assert.Contains(all, d => d.Severity == "Error");
            Assert.Equal(all.Count, all.DistinctBy(d => (d.Id, d.Message, d.StartLine, d.StartCharacter)).Count());
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task project_diagnostics_rejects_stale_cursor()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithDiagnostics());

            await OpenUntilReadyAsync(fx, solution);

            var list = await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>());
            var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list);
            var projectId = Assert.Single(projects.Projects).ProjectId;

            var stale = MemberPageCursor.Encode(epoch: 999, offset: 0);

            var result = await fx.Client.CallToolAsync(
                "project_diagnostics",
                new Dictionary<string, object?>
                {
                    ["projectId"] = projectId,
                    ["cursor"] = stale
                });

            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.StaleCursor, body.Error);
            Assert.Contains("project_diagnostics", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("without", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
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
