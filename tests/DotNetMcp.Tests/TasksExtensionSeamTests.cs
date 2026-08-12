#pragma warning disable MCPEXP001

using System.Text.Json;
using DotNetMcp.Server;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace DotNetMcp.Tests;

public class TasksExtensionSeamTests
{
    [Fact]
    public async Task non_tasks_client_still_uses_workspace_open_and_status()
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

            WorkspaceStatusDto? latest = null;
            for (var i = 0; i < 40; i++)
            {
                var poll = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
                latest = InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(poll);
                if (latest.Phase == "ready")
                {
                    break;
                }

                await Task.Delay(50);
            }

            Assert.Equal("ready", latest?.Phase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task tasks_call_workspace_status_completes_via_tasks_get()
    {
        await using var fx = new InProcessMcpFixture();
        var raw = await fx.Client.CallToolAsTaskAsync(new CallToolRequestParams
        {
            Name = "workspace_status",
            Arguments = new Dictionary<string, JsonElement>()
        });

        Assert.True(raw.IsTask, "Client opt-in should create a task when server has WithTasks");
        var created = raw.TaskCreated!;
        Assert.False(string.IsNullOrWhiteSpace(created.TaskId));

        GetTaskResult? terminal = null;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(100);
            terminal = await fx.Client.GetTaskAsync(created.TaskId);
            if (terminal is CompletedTaskResult or FailedTaskResult or CancelledTaskResult)
            {
                break;
            }
        }

        Assert.IsType<CompletedTaskResult>(terminal);
    }

    [Fact]
    public async Task tasks_cancel_reaches_tool_cancellation_token()
    {
        await using var fx = InProcessMcpFixture.CreateWithCancelProbe();
        var observation = fx.GetRequiredService<TasksCancelProbeObservation>();
        var raw = await fx.Client.CallToolAsTaskAsync(new CallToolRequestParams
        {
            Name = "tasks_cancel_probe",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["seconds"] = JsonSerializer.SerializeToElement(30)
            }
        });
        Assert.True(raw.IsTask);
        var taskId = raw.TaskCreated!.TaskId;
        await Task.Delay(100);
        await fx.Client.CancelTaskAsync(taskId);

        GetTaskResult? terminal = null;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(50);
            terminal = await fx.Client.GetTaskAsync(taskId);
            if (terminal is CancelledTaskResult or FailedTaskResult or CompletedTaskResult)
            {
                break;
            }
        }

        Assert.IsType<CancelledTaskResult>(terminal);

        var sawCancelled = false;
        for (var i = 0; i < 40; i++)
        {
            if (observation.SawCancelled)
            {
                sawCancelled = true;
                break;
            }

            await Task.Delay(50);
        }

        Assert.True(sawCancelled, "tasks/cancel must cancel the tool CancellationToken");
    }

    private static string CreateTempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnet-mcp-{prefix}-{Guid.NewGuid():N}");
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
