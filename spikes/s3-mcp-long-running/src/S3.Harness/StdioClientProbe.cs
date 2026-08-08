#pragma warning disable MCPEXP001

using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace S3.Harness;

/// <summary>
/// Drives the real stdio S3.Server process — closest automated stand-in for Claude Code / Cursor transport.
/// </summary>
public static class StdioClientProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: S3.Harness stdio <scenario> [serverDllOrProject]");
            return 2;
        }

        var scenario = args[1];
        var serverTarget = args.Length > 2
            ? args[2]
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "S3.Server", "bin", "Debug", "net8.0", "S3.Server.dll"));

        await using var client = await ConnectAsync(serverTarget);
        return scenario switch
        {
            "timeout-60" => await TimeoutAsync(client, sleepSeconds: 90, timeoutMs: 60_000),
            "timeout-short" => await TimeoutAsync(client, sleepSeconds: 5, timeoutMs: 1_000),
            "progress-60" => await ProgressTimeoutAsync(client, sleepSeconds: 90, timeoutMs: 60_000),
            "manual" => await ManualAsync(client),
            "tasks" => await TasksAsync(client),
            "list" => await ListAsync(client),
            _ => 2
        };
    }

    private static async Task<McpClient> ConnectAsync(string serverTarget)
    {
        string command;
        string[] arguments;
        if (serverTarget.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            command = "dotnet";
            arguments = [serverTarget];
        }
        else if (serverTarget.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            command = "dotnet";
            arguments = ["run", "--project", serverTarget, "--no-build"];
        }
        else
        {
            command = serverTarget;
            arguments = [];
        }

        Console.WriteLine($"stdio transport: {command} {string.Join(' ', arguments)}");
        return await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "s3-long-running",
            Command = command,
            Arguments = arguments
        }));
    }

    private static async Task<int> ListAsync(McpClient client)
    {
        Console.WriteLine($"server={client.ServerInfo?.Name} {client.ServerInfo?.Version}");
        Console.WriteLine($"protocol={client.NegotiatedProtocolVersion}");
        Console.WriteLine($"caps={System.Text.Json.JsonSerializer.Serialize(client.ServerCapabilities)}");
        var tools = await client.ListToolsAsync();
        foreach (var t in tools)
        {
            Console.WriteLine($"- {t.Name}: {t.Description}");
        }

        return 0;
    }

    private static async Task<int> TimeoutAsync(McpClient client, int sleepSeconds, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await client.CallToolAsync(
                "sleep_long",
                new Dictionary<string, object?> { ["seconds"] = sleepSeconds },
                cancellationToken: cts.Token);
            sw.Stop();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                outcome = "completed",
                elapsedMs = sw.ElapsedMilliseconds,
                text = InProcessMcpFixture.TextOf(result)
            }));
            return 0;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                outcome = "error",
                elapsedMs = sw.ElapsedMilliseconds,
                timeoutMs,
                sleepSeconds,
                exceptionType = ex.GetType().FullName,
                message = ex.Message
            }));
            return 0; // observational
        }
    }

    private static async Task<int> ProgressTimeoutAsync(McpClient client, int sleepSeconds, int timeoutMs)
    {
        var reports = 0;
        var progress = new Progress<ProgressNotificationValue>(_ => Interlocked.Increment(ref reports));
        using var cts = new CancellationTokenSource(timeoutMs);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await client.CallToolAsync(
                "sleep_with_progress",
                new Dictionary<string, object?> { ["seconds"] = sleepSeconds },
                progress,
                cancellationToken: cts.Token);
            sw.Stop();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                outcome = "completed",
                elapsedMs = sw.ElapsedMilliseconds,
                progressReports = reports,
                text = InProcessMcpFixture.TextOf(result)
            }));
            return 0;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                outcome = "error",
                elapsedMs = sw.ElapsedMilliseconds,
                timeoutMs,
                sleepSeconds,
                progressReports = reports,
                exceptionType = ex.GetType().FullName,
                message = ex.Message,
                note = "C# client cancels via CancellationToken; progress does not extend this CTS. TS SDK defaults resetTimeoutOnProgress=false."
            }));
            return 0;
        }
    }

    private static async Task<int> ManualAsync(McpClient client)
    {
        var open = await client.CallToolAsync("slow_open", new Dictionary<string, object?>
        {
            ["seconds"] = 3,
            ["units"] = 6
        });
        Console.WriteLine($"open: {InProcessMcpFixture.TextOf(open)}");
        var jobId = JsonDocument.Parse(InProcessMcpFixture.TextOf(open)).RootElement.GetProperty("jobId").GetString();
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(200);
            var poll = await client.CallToolAsync("slow_status", new Dictionary<string, object?> { ["jobId"] = jobId });
            var text = InProcessMcpFixture.TextOf(poll);
            Console.WriteLine($"poll: {text}");
            if (text.Contains("\"phase\":\"ready\"", StringComparison.Ordinal))
            {
                return 0;
            }
        }

        return 1;
    }

    private static async Task<int> TasksAsync(McpClient client)
    {
        var raw = await client.CallToolAsTaskAsync(new CallToolRequestParams
        {
            Name = "sleep_long",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["seconds"] = JsonSerializer.SerializeToElement(3)
            }
        });
        if (!raw.IsTask)
        {
            Console.WriteLine("no task created — client did not opt in or server rejected");
            return 1;
        }

        var id = raw.TaskCreated!.TaskId;
        Console.WriteLine($"task={id}");
        for (var i = 0; i < 50; i++)
        {
            await Task.Delay(200);
            var state = await client.GetTaskAsync(id);
            Console.WriteLine($"poll {i}: {state.GetType().Name}");
            if (state is CompletedTaskResult or FailedTaskResult or CancelledTaskResult)
            {
                return state is CompletedTaskResult ? 0 : 1;
            }
        }

        return 1;
    }
}
