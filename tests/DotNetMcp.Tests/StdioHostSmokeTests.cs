using ModelContextProtocol.Client;

namespace DotNetMcp.Tests;

public class StdioHostSmokeTests
{
    [Fact]
    public async Task stdio_server_process_lists_tools()
    {
        // Launch the server DLL copied next to the test assembly (same Configuration/TFM).
        // Avoid `dotnet run --project --no-build`, which defaults to Debug and fails when CI builds Release only.
        var serverDll = Path.Combine(AppContext.BaseDirectory, "DotNetMcp.Server.dll");

        Assert.True(File.Exists(serverDll), $"Server assembly not found: {serverDll}");

        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "dotnet-mcp",
            Command = "dotnet",
            Arguments = [serverDll]
        }));

        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "workspace_open");
    }
}
