using ModelContextProtocol.Client;

namespace DotNetMcp.Tests;

public class StdioHostSmokeTests
{
    [Fact]
    public async Task stdio_server_process_lists_tools()
    {
        var serverProject = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "DotNetMcp.Server", "DotNetMcp.Server.csproj"));

        Assert.True(File.Exists(serverProject), $"Server project not found: {serverProject}");

        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "dotnet-mcp",
            Command = "dotnet",
            Arguments = ["run", "--project", serverProject, "--no-build"]
        }));

        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "workspace_open");
    }
}
