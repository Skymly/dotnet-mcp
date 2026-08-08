#pragma warning disable MCPEXP001

using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using S3.Core;

namespace S3.Harness;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: S3.Harness <scenario>");
            Console.Error.WriteLine("  scenarios: manual | tasks | timeout | progress | concurrent | cancel | soft-budget | all");
            Console.Error.WriteLine("  stdio <timeout-60|timeout-short|progress-60|manual|tasks|list> [serverDll]");
            return 2;
        }

        if (args[0] == "stdio")
        {
            return await StdioClientProbe.RunAsync(args);
        }

        var scenario = args[0];
        return scenario switch
        {
            "manual" => await WithFixture(RunManualAsync),
            "tasks" => await WithFixture(RunTasksAsync),
            "timeout" => await WithFixture(RunTimeoutAsync),
            "progress" => await WithFixture(RunProgressAsync),
            "concurrent" => await WithFixture(RunConcurrentAsync),
            "cancel" => await WithFixture(RunCancelAsync),
            "soft-budget" => await WithFixture(RunSoftBudgetAsync),
            "all" => await RunAllAsync(),
            _ => FailUnknown(scenario)
        };
    }

    private static async Task<int> WithFixture(Func<InProcessMcpFixture, Task<int>> run)
    {
        await using var fx = new InProcessMcpFixture();
        return await run(fx);
    }

    private static int FailUnknown(string scenario)
    {
        Console.Error.WriteLine($"Unknown scenario: {scenario}");
        return 2;
    }

    private static async Task<int> RunAllAsync()
    {
        foreach (var (name, run) in new (string, Func<InProcessMcpFixture, Task<int>>)[]
                 {
                     ("manual", RunManualAsync),
                     ("tasks", RunTasksAsync),
                     ("timeout", RunTimeoutAsync),
                     ("progress", RunProgressAsync),
                     ("concurrent", RunConcurrentAsync),
                     ("cancel", RunCancelAsync),
                     ("soft-budget", RunSoftBudgetAsync)
                 })
        {
            await using var fx = new InProcessMcpFixture();
            var code = await run(fx);
            Console.WriteLine($"[{name}] exit={code}");
            if (code != 0)
            {
                return code;
            }
        }

        return 0;
    }

    private static async Task<int> RunManualAsync(InProcessMcpFixture fx)
    {
        var open = await fx.Client.CallToolAsync("slow_open", new Dictionary<string, object?>
        {
            ["seconds"] = 1,
            ["units"] = 5
        });
        var status = InProcessMcpFixture.Deserialize<SlowJobStatusDto>(open);
        Console.WriteLine($"open: {JsonSerializer.Serialize(status)}");
        SlowJobStatusDto? latest = null;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(50);
            var poll = await fx.Client.CallToolAsync("slow_status", new Dictionary<string, object?>
            {
                ["jobId"] = status.JobId
            });
            latest = InProcessMcpFixture.Deserialize<SlowJobStatusDto>(poll);
            Console.WriteLine($"poll {i}: phase={latest.Phase} {latest.CompletedUnits}/{latest.TotalUnits}");
            if (latest.Phase is "ready" or "failed" or "cancelled")
            {
                break;
            }
        }

        Console.WriteLine($"final suggestedAction={latest?.SuggestedAction}");
        return latest?.Phase == "ready" ? 0 : 1;
    }

    private static async Task<int> RunTasksAsync(InProcessMcpFixture fx)
    {
        var raw = await fx.Client.CallToolAsTaskAsync(new CallToolRequestParams
        {
            Name = "sleep_long",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["seconds"] = JsonSerializer.SerializeToElement(2)
            }
        });

        if (!raw.IsTask)
        {
            Console.WriteLine($"inline (no task): {InProcessMcpFixture.TextOf(raw.Result!)}");
            return 1;
        }

        var created = raw.TaskCreated!;
        Console.WriteLine($"task created id={created.TaskId} status={created.Status}");
        var pollMs = created.PollIntervalMs ?? 100;
        for (var i = 0; i < 50; i++)
        {
            await Task.Delay((int)pollMs);
            var state = await fx.Client.GetTaskAsync(created.TaskId);
            Console.WriteLine($"poll {i}: {state.GetType().Name}");
            if (state is CompletedTaskResult completed)
            {
                Console.WriteLine($"completed: {completed.Result}");
                return 0;
            }

            if (state is FailedTaskResult or CancelledTaskResult)
            {
                return 1;
            }
        }

        return 1;
    }

    private static async Task<int> RunTimeoutAsync(InProcessMcpFixture fx)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await fx.Client.CallToolAsync(
                "sleep_long",
                new Dictionary<string, object?> { ["seconds"] = 5 },
                cancellationToken: cts.Token);
            Console.WriteLine("unexpected success");
            return 1;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            Console.WriteLine($"client cancelled after {sw.ElapsedMilliseconds}ms (simulates timeout)");
            await Task.Delay(200);
            var dump = await fx.Client.CallToolAsync("dump_observations");
            Console.WriteLine(InProcessMcpFixture.TextOf(dump));
            return 0;
        }
    }

    private static async Task<int> RunProgressAsync(InProcessMcpFixture fx)
    {
        var reports = new List<ProgressNotificationValue>();
        var progress = new Progress<ProgressNotificationValue>(v =>
        {
            reports.Add(v);
            Console.WriteLine($"progress {v.Progress}/{v.Total}: {v.Message}");
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        try
        {
            await fx.Client.CallToolAsync(
                "sleep_with_progress",
                new Dictionary<string, object?> { ["seconds"] = 5 },
                progress,
                cancellationToken: cts.Token);
            Console.WriteLine("completed without client cancel");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"cancelled after progress reports={reports.Count}");
        }

        Console.WriteLine($"reports={reports.Count}");
        return reports.Count > 0 ? 0 : 1;
    }

    private static async Task<int> RunConcurrentAsync(InProcessMcpFixture fx)
    {
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
        foreach (var r in results)
        {
            Console.WriteLine(InProcessMcpFixture.TextOf(r));
        }

        var a = InProcessMcpFixture.Deserialize<ConcurrentProbeResultDto>(results[0]);
        var b = InProcessMcpFixture.Deserialize<ConcurrentProbeResultDto>(results[1]);
        var aStart = DateTimeOffset.Parse(a.StartedAtUtc);
        var bStart = DateTimeOffset.Parse(b.StartedAtUtc);
        var aEnd = DateTimeOffset.Parse(a.FinishedAtUtc);
        var bEnd = DateTimeOffset.Parse(b.FinishedAtUtc);
        var overlapped = aStart < bEnd && bStart < aEnd;
        Console.WriteLine($"overlapped={overlapped} threads=({a.ManagedThreadId},{b.ManagedThreadId})");
        return overlapped ? 0 : 1;
    }

    private static async Task<int> RunCancelAsync(InProcessMcpFixture fx)
    {
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
        await Task.Delay(200);
        await fx.Client.SendNotificationAsync(
            NotificationMethods.CancelledNotification,
            new CancelledNotificationParams { RequestId = requestId });
        try
        {
            await invoke;
            Console.WriteLine("unexpected success");
            return 1;
        }
        catch (OperationCanceledException)
        {
            var saw = false;
            for (var i = 0; i < 40; i++)
            {
                saw = fx.ObservationLog.Snapshot()
                    .Any(e => e.Category == "sleep_long" && e.Message == "cancelled");
                if (saw)
                {
                    break;
                }

                await Task.Delay(50);
            }

            Console.WriteLine(fx.ObservationLog.ToJsonLines());
            Console.WriteLine($"serverSawCancel={saw}");
            return saw ? 0 : 1;
        }
    }

    private static async Task<int> RunSoftBudgetAsync(InProcessMcpFixture fx)
    {
        var page1 = InProcessMcpFixture.Deserialize<SoftBudgetPageDto>(
            await fx.Client.CallToolAsync("soft_budget_page", new Dictionary<string, object?>
            {
                ["pageSize"] = 50,
                ["totalItems"] = 100,
                ["budgetMs"] = 25,
                ["itemCostMs"] = 10
            }));
        Console.WriteLine(JsonSerializer.Serialize(page1));
        if (!page1.Truncated || page1.NextCursor is null)
        {
            return 1;
        }

        var page2 = InProcessMcpFixture.Deserialize<SoftBudgetPageDto>(
            await fx.Client.CallToolAsync("soft_budget_page", new Dictionary<string, object?>
            {
                ["cursor"] = page1.NextCursor,
                ["pageSize"] = 50,
                ["totalItems"] = 100,
                ["budgetMs"] = 25,
                ["itemCostMs"] = 10
            }));
        Console.WriteLine(JsonSerializer.Serialize(page2));
        return page2.Items.Count > 0 ? 0 : 1;
    }
}
