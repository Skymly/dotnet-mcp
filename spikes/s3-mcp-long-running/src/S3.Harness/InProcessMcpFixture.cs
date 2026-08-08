#pragma warning disable MCPEXP001

using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using S3.Core;
using S3.Server;

namespace S3.Harness;

/// <summary>
/// In-process MCP client/server over pipes — the primary automated seam for Spike S3.
/// </summary>
public sealed class InProcessMcpFixture : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly McpServer _server;
    private readonly CancellationTokenSource _serverCts = new();
    private readonly Task _serverTask;

    public InProcessMcpFixture()
    {
        Pipe clientToServer = new(), serverToClient = new();
        var taskStore = new InMemoryMcpTaskStore { DefaultPollIntervalMs = 100 };

        var services = new ServiceCollection();
        services.AddSingleton<SlowJobStore>();
        services.AddSingleton<ObservationLog>();
        services.AddSingleton<SpikeTools>();
        services.AddMcpServer()
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithTools<SpikeTools>()
            .WithTasks(taskStore);

        _services = services.BuildServiceProvider();
        _server = _services.GetRequiredService<McpServer>();
        _serverTask = _server.RunAsync(_serverCts.Token);

        Client = McpClient.CreateAsync(
            new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream())).GetAwaiter().GetResult();

        ObservationLog = _services.GetRequiredService<ObservationLog>();
        Jobs = _services.GetRequiredService<SlowJobStore>();
        TaskStore = taskStore;
    }

    public McpClient Client { get; }
    public ObservationLog ObservationLog { get; }
    public SlowJobStore Jobs { get; }
    public InMemoryMcpTaskStore TaskStore { get; }

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
        await _services.DisposeAsync();
        _serverCts.Dispose();
    }
}
