#pragma warning disable MCPEXP001

using System.IO.Pipelines;
using System.Text.Json;
using DotNetMcp.Core;
using DotNetMcp.Server;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotNetMcp.Tests;

/// <summary>
/// In-process MCP client/server over pipes — primary automated seam for product tests.
/// </summary>
public sealed class InProcessMcpFixture : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly McpServer _server;
    private readonly CancellationTokenSource _serverCts = new();
    private readonly Task _serverTask;

    public InProcessMcpFixture(
        TrustedRoots? trustedRoots = null,
        ISolutionLoader? solutionLoader = null,
        WorkspaceHostOptions? workspaceHostOptions = null,
        SoftBudgetOptions? softBudgetOptions = null,
        AuditOptions? auditOptions = null,
        IAuditLogger? auditLogger = null)
        : this(
            trustedRoots,
            solutionLoader,
            workspaceHostOptions,
            softBudgetOptions,
            auditOptions,
            auditLogger,
            configure: null)
    {
    }

    private InProcessMcpFixture(
        TrustedRoots? trustedRoots,
        ISolutionLoader? solutionLoader,
        WorkspaceHostOptions? workspaceHostOptions,
        SoftBudgetOptions? softBudgetOptions,
        AuditOptions? auditOptions,
        IAuditLogger? auditLogger,
        Action<IMcpServerBuilder>? configure)
    {
        Pipe clientToServer = new(), serverToClient = new();
        var roots = trustedRoots ?? TrustedRoots.Create([Directory.GetCurrentDirectory()]);
        var taskStore = new InMemoryMcpTaskStore { DefaultPollIntervalMs = 250 };

        var services = new ServiceCollection();
        ServerHost.AddDotNetMcp(
            services,
            roots,
            solutionLoader,
            workspaceHostOptions,
            softBudgetOptions ?? SoftBudgetOptions.Default,
            auditOptions ?? AuditOptions.Default,
            auditLogger);

        var mcp = services.AddMcpServer()
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithToolsFromAssembly(typeof(ServerHost).Assembly)
            .WithTasks(taskStore);
        configure?.Invoke(mcp);

        _services = services.BuildServiceProvider();
        _server = _services.GetRequiredService<McpServer>();
        WorkspaceHost = _services.GetRequiredService<WorkspaceHost>();
        WorkspaceEdit = _services.GetRequiredService<WorkspaceEdit>();
        _serverTask = _server.RunAsync(_serverCts.Token);

        Client = McpClient.CreateAsync(
            new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream())).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Product fixture plus cancel-probe tools (test assembly only). Does not change Server assembly surface.
    /// </summary>
    public static InProcessMcpFixture CreateWithCancelProbe() =>
        new(
            trustedRoots: null,
            solutionLoader: null,
            workspaceHostOptions: null,
            softBudgetOptions: null,
            auditOptions: null,
            auditLogger: null,
            configure: builder =>
            {
                builder.Services.AddSingleton<TasksCancelProbeObservation>();
                builder.Services.AddSingleton<TasksCancelProbeTools>();
                builder.WithTools<TasksCancelProbeTools>();
            });

    public McpClient Client { get; }

    public WorkspaceHost WorkspaceHost { get; }

    public WorkspaceEdit WorkspaceEdit { get; }

    public T GetRequiredService<T>()
        where T : notnull =>
        _services.GetRequiredService<T>();

    public static string TextOf(CallToolResult result)
    {
        var block = result.Content.OfType<TextContentBlock>().FirstOrDefault()
            ?? throw new InvalidOperationException("Expected text content.");
        return block.Text ?? string.Empty;
    }

    public static T Deserialize<T>(CallToolResult result) =>
        JsonSerializer.Deserialize<T>(TextOf(result), JsonOptions.Default)
        ?? throw new InvalidOperationException("Failed to deserialize tool result.");

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        _serverCts.Cancel();
        try
        {
            await _serverTask;
        }
        catch (OperationCanceledException)
        {
        }

        await _server.DisposeAsync();

        var host = _services.GetService<WorkspaceHost>();
        if (host is not null)
        {
            await host.DisposeAsync();
        }

        await _services.DisposeAsync();
        _serverCts.Dispose();
    }
}
