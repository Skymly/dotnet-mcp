#pragma warning disable MCPEXP001

using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using DotNetMcp.Core;
using DotNetMcp.Server;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotNetMcp.Bench;

/// <summary>
/// In-process MCP client/server over pipes — same envelope as product tests, no test assembly dependency.
/// </summary>
internal sealed class McpBenchHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly McpServer _server;
    private readonly CancellationTokenSource _serverCts = new();
    private readonly Task _serverTask;

    public McpBenchHost(IReadOnlyList<string> trustedRoots)
    {
        Pipe clientToServer = new(), serverToClient = new();
        var roots = TrustedRoots.Create(trustedRoots);
        var taskStore = new InMemoryMcpTaskStore { DefaultPollIntervalMs = 250 };

        var services = new ServiceCollection();
        ServerHost.AddDotNetMcp(
            services,
            roots,
            solutionLoader: new MsBuildSolutionLoader(TrustedRoots.Create([Directory.GetCurrentDirectory()])),
            workspaceHostOptions: WorkspaceHostOptions.Default,
            softBudgetOptions: SoftBudgetOptions.FromEnvironment(),
            auditOptions: new AuditOptions { Enabled = false });

        services.AddMcpServer()
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithToolsFromAssembly(typeof(ServerHost).Assembly)
            .WithTasks(taskStore);

        _services = services.BuildServiceProvider();
        _server = _services.GetRequiredService<McpServer>();
        _serverTask = _server.RunAsync(_serverCts.Token);

        Client = McpClient.CreateAsync(
            new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream())).GetAwaiter().GetResult();
    }

    public McpClient Client { get; }

    public static string TextOf(CallToolResult result)
    {
        var block = result.Content.OfType<TextContentBlock>().FirstOrDefault()
            ?? throw new InvalidOperationException("Expected text content.");
        return block.Text ?? string.Empty;
    }

    public static T Deserialize<T>(CallToolResult result) =>
        JsonSerializer.Deserialize<T>(TextOf(result), JsonOptions.Default)
        ?? throw new InvalidOperationException("Failed to deserialize tool result.");

    public async Task<CallToolResult> CallAsync(
        string tool,
        IReadOnlyDictionary<string, object?>? args = null,
        CancellationToken cancellationToken = default) =>
        await Client.CallToolAsync(tool, args ?? new Dictionary<string, object?>(), cancellationToken: cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        _serverCts.Cancel();
        try
        {
            await _serverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _server.DisposeAsync().ConfigureAwait(false);

        var host = _services.GetService<WorkspaceHost>();
        if (host is not null)
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }

        await _services.DisposeAsync().ConfigureAwait(false);
        _serverCts.Dispose();
    }
}

internal static class FixturePaths
{
    public static string Root { get; } = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures"));

    public static string SampleSlnx => Path.Combine(Root, "SampleFilter", "Sample.slnx");
    public static string MixedWithFsSlnx => Path.Combine(Root, "MixedCsharpVb", "MixedWithFs.slnx");
    public static string AvaloniaProject => Path.Combine(Root, "AvaloniaApp", "AvaloniaApp.csproj");
    public static string AvaloniaMainWindow => Path.Combine(Root, "AvaloniaApp", "MainWindow.axaml");
}

internal static class BudgetClass
{
    public const string OpenReturn = "open-return";
    public const string OpenReady = "open-ready";
    public const string SingleProject = "single-project";
    public const string FindRefsScoped = "find-refs-scoped";
    public const string FindRefsEntire = "find-refs-entire";
    public const string BatchDiagnostics = "batch-diagnostics";
    public const string FixAll = "fix-all";
    public const string Unbounded = "unbounded";

    public static double Milliseconds(string budgetClass) => budgetClass switch
    {
        OpenReturn => 500,
        OpenReady => 60_000,
        SingleProject => SoftBudgetOptions.Default.SingleProjectCompile.TotalMilliseconds,
        FindRefsScoped => SoftBudgetOptions.Default.FindRefsScoped.TotalMilliseconds,
        FindRefsEntire => SoftBudgetOptions.Default.FindRefsEntireSolution.TotalMilliseconds,
        BatchDiagnostics => SoftBudgetOptions.Default.BatchDiagnostics.TotalMilliseconds,
        FixAll => SoftBudgetOptions.Default.FixAllProject.TotalMilliseconds,
        _ => 60_000,
    };
}

internal sealed record ToolObservation
{
    public required bool IsError { get; init; }
    public required string Payload { get; init; }
    public required int PayloadBytes { get; init; }
    public int ResultCount { get; init; }
    public bool Truncated { get; init; }
    public bool HasNextCursor { get; init; }
    public string? Error { get; init; }
    public string? Handle { get; init; }

    public static ToolObservation From(CallToolResult result)
    {
        var payload = McpBenchHost.TextOf(result);
        var observation = new ToolObservation
        {
            IsError = result.IsError is true,
            Payload = payload,
            PayloadBytes = System.Text.Encoding.UTF8.GetByteCount(payload),
        };

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            return observation with
            {
                ResultCount = CountResults(root),
                Truncated = root.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True,
                HasNextCursor = root.TryGetProperty("nextCursor", out var cursor) &&
                    cursor.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(cursor.GetString()),
                Error = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : null,
                Handle = root.TryGetProperty("handle", out var handle) && handle.ValueKind == JsonValueKind.String
                    ? handle.GetString()
                    : null,
            };
        }
        catch (JsonException)
        {
            return observation;
        }
    }

    private static int CountResults(JsonElement root)
    {
        foreach (var name in new[] { "items", "projects", "locations", "documents" })
        {
            if (root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
            {
                return array.GetArrayLength();
            }
        }

        return root.TryGetProperty("handle", out _) ? 1 : 0;
    }
}

internal sealed class WorkspaceCase
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required McpBenchHost Host { get; init; }
    public IReadOnlyList<ProjectSummaryDto> Projects { get; set; } = [];
    public WorkspaceStatusDto? Status { get; set; }

    public string? FindProjectId(string nameFragment, string? language = null) =>
        Projects.FirstOrDefault(p =>
            p.Name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase) &&
            (language is null || string.Equals(p.Language, language, StringComparison.OrdinalIgnoreCase)))
            ?.ProjectId;

    public async Task<string?> ResolveHandleAsync(string name, string? projectId = null)
    {
        var result = await Host.CallAsync(
            "symbol_resolve",
            new Dictionary<string, object?> { ["name"] = name, ["projectId"] = projectId }).ConfigureAwait(false);
        var observation = ToolObservation.From(result);
        return observation.IsError ? null : observation.Handle;
    }
}

internal static class WorkspacePrep
{
    public static int CleanBinObj(string root)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            var name = System.IO.Path.GetFileName(dir);
            if (name is not ("bin" or "obj"))
            {
                continue;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
                removed++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    public static async Task<(double ReturnMs, WorkspaceStatusDto Status, double ReadyMs)> OpenUntilReadyAsync(
        McpBenchHost host,
        string path,
        TimeSpan timeout)
    {
        var openWatch = Stopwatch.StartNew();
        var open = await host.CallAsync("workspace_open", new Dictionary<string, object?> { ["path"] = path })
            .ConfigureAwait(false);
        openWatch.Stop();
        if (open.IsError is true)
        {
            throw new InvalidOperationException($"workspace_open failed: {McpBenchHost.TextOf(open)}");
        }

        var readyWatch = Stopwatch.StartNew();
        var deadline = DateTime.UtcNow + timeout;
        WorkspaceStatusDto? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var statusResult = await host.CallAsync("workspace_status").ConfigureAwait(false);
            if (statusResult.IsError is true)
            {
                throw new InvalidOperationException($"workspace_status failed: {McpBenchHost.TextOf(statusResult)}");
            }

            last = McpBenchHost.Deserialize<WorkspaceStatusDto>(statusResult);
            if (last.Phase is "ready" or "failed" or "cancelled")
            {
                break;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        readyWatch.Stop();
        if (last is null)
        {
            throw new TimeoutException($"workspace_status produced no payload for {path}");
        }

        if (last.Phase != "ready")
        {
            throw new InvalidOperationException($"Workspace '{path}' ended in phase '{last.Phase}': {last.Error}");
        }

        return (openWatch.Elapsed.TotalMilliseconds, last, readyWatch.Elapsed.TotalMilliseconds);
    }
}


