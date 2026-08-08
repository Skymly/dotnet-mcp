using System.IO.Pipelines;
using System.Text.Json;
using DotNetMcp.Server;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
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

    public InProcessMcpFixture(TrustedRoots? trustedRoots = null)
    {
        Pipe clientToServer = new(), serverToClient = new();
        var roots = trustedRoots ?? TrustedRoots.Create([Directory.GetCurrentDirectory()]);

        var services = new ServiceCollection();
        ServerHost.AddDotNetMcp(services, roots);
        services.AddMcpServer()
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithToolsFromAssembly(typeof(ServerHost).Assembly);

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
