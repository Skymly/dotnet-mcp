#pragma warning disable MCPEXP001

using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using S3.Core;
using S3.Harness;
using Xunit;

namespace S3.Tests;

public class ProtocolSeamTests
{
    [Fact]
    public async Task Manual_mode_slow_open_returns_immediately_and_status_reaches_ready()
    {
        await using var fx = new InProcessMcpFixture();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var open = await fx.Client.CallToolAsync("slow_open", new Dictionary<string, object?>
        {
            ["seconds"] = 1,
            ["units"] = 5
        });
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500, $"slow_open blocked for {sw.ElapsedMilliseconds}ms");

        var status = InProcessMcpFixture.Deserialize<SlowJobStatusDto>(open);
        Assert.False(string.IsNullOrWhiteSpace(status.JobId));
        Assert.Contains("slow_status", status.SuggestedAction);

        SlowJobStatusDto? latest = null;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(50);
            var poll = await fx.Client.CallToolAsync("slow_status", new Dictionary<string, object?>
            {
                ["jobId"] = status.JobId
            });
            latest = InProcessMcpFixture.Deserialize<SlowJobStatusDto>(poll);
            if (latest.Phase == "ready")
            {
                break;
            }
        }

        Assert.Equal("ready", latest?.Phase);
    }

    [Fact]
    public async Task Tasks_extension_creates_task_and_completes_via_poll()
    {
        await using var fx = new InProcessMcpFixture();
        var raw = await fx.Client.CallToolAsTaskAsync(new CallToolRequestParams
        {
            Name = "sleep_long",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["seconds"] = System.Text.Json.JsonSerializer.SerializeToElement(1)
            }
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
    public async Task Concurrent_tool_calls_overlap_on_server()
    {
        await using var fx = new InProcessMcpFixture();
        var t1 = fx.Client.CallToolAsync("concurrent_probe", new Dictionary<string, object?>
        {
            ["label"] = "a",
            ["holdMs"] = 400
        }).AsTask();
        var t2 = fx.Client.CallToolAsync("concurrent_probe", new Dictionary<string, object?>
        {
            ["label"] = "b",
            ["holdMs"] = 400
        }).AsTask();
        var results = await Task.WhenAll(t1, t2);
        var a = InProcessMcpFixture.Deserialize<ConcurrentProbeResultDto>(results[0]);
        var b = InProcessMcpFixture.Deserialize<ConcurrentProbeResultDto>(results[1]);
        var aStart = DateTimeOffset.Parse(a.StartedAtUtc);
        var bStart = DateTimeOffset.Parse(b.StartedAtUtc);
        var aEnd = DateTimeOffset.Parse(a.FinishedAtUtc);
        var bEnd = DateTimeOffset.Parse(b.FinishedAtUtc);
        Assert.True(aStart < bEnd && bStart < aEnd, "Handlers should overlap (fire-and-forget dispatch)");
    }

    [Fact]
    public async Task Explicit_cancelled_notification_propagates_to_tool_token()
    {
        await using var fx = new InProcessMcpFixture();
        var requestId = new RequestId(Guid.NewGuid().ToString("N"));
        var invoke = fx.Client.SendRequestAsync<CallToolRequestParams, CallToolResult>(
            RequestMethods.ToolsCall,
            new CallToolRequestParams
            {
                Name = "sleep_long",
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["seconds"] = JsonSerializer.SerializeToElement(30)
                }
            },
            requestId: requestId).AsTask();

        await Task.Delay(150);
        await fx.Client.SendNotificationAsync(
            NotificationMethods.CancelledNotification,
            new CancelledNotificationParams { RequestId = requestId });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await invoke);

        var sawCancelled = false;
        for (var i = 0; i < 40; i++)
        {
            sawCancelled = fx.ObservationLog.Snapshot()
                .Any(e => e.Category == "sleep_long" && e.Message == "cancelled");
            if (sawCancelled)
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.True(sawCancelled, "Explicit notifications/cancelled must cancel the tool token");
    }

    [Fact]
    public async Task Tasks_cancel_marks_task_cancelled()
    {
        await using var fx = new InProcessMcpFixture();
        var raw = await fx.Client.CallToolAsTaskAsync(new CallToolRequestParams
        {
            Name = "sleep_long",
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
    }

    [Fact]
    public async Task Soft_budget_page_returns_cursor_for_continuation()
    {
        await using var fx = new InProcessMcpFixture();
        var page1 = InProcessMcpFixture.Deserialize<SoftBudgetPageDto>(
            await fx.Client.CallToolAsync("soft_budget_page", new Dictionary<string, object?>
            {
                ["pageSize"] = 50,
                ["totalItems"] = 100,
                ["budgetMs"] = 25,
                ["itemCostMs"] = 10
            }));
        Assert.True(page1.Truncated);
        Assert.False(string.IsNullOrEmpty(page1.NextCursor));
        Assert.Contains("nextCursor", page1.Message);

        var page2 = InProcessMcpFixture.Deserialize<SoftBudgetPageDto>(
            await fx.Client.CallToolAsync("soft_budget_page", new Dictionary<string, object?>
            {
                ["cursor"] = page1.NextCursor,
                ["budgetMs"] = 25,
                ["itemCostMs"] = 10,
                ["totalItems"] = 100
            }));
        Assert.NotEmpty(page2.Items);
        Assert.StartsWith("item-", page2.Items[0]);
        Assert.NotEqual(page1.Items[0], page2.Items[0]);
    }

    [Fact]
    public async Task Progress_notifications_are_delivered_while_tool_runs()
    {
        await using var fx = new InProcessMcpFixture();
        var reports = new List<ProgressNotificationValue>();
        var progress = new Progress<ProgressNotificationValue>(reports.Add);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1600));
        try
        {
            await fx.Client.CallToolAsync(
                "sleep_with_progress",
                new Dictionary<string, object?> { ["seconds"] = 5 },
                progress,
                cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // expected — we cancel mid-flight after receiving progress
        }

        Assert.NotEmpty(reports);
    }
}
