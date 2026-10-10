using DotNetMcp.Server;
using ModelContextProtocol.Client;

namespace DotNetMcp.Tests;

public class StdioHostSmokeTests
{
    [Fact]
    public async Task stdio_server_process_lists_tools()
    {
        using var timeout = new CancellationTokenSource(WorkspaceReady.DefaultTimeout);
        await using var client = await ConnectAsync(Path.GetTempPath(), timeout.Token);

        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Contains(tools, t => t.Name == "workspace_open");
    }

    [Fact]
    public async Task stdio_server_process_calls_status_and_returns_not_ready_error()
    {
        using var timeout = new CancellationTokenSource(WorkspaceReady.DefaultTimeout);
        await using var client = await ConnectAsync(Path.GetTempPath(), timeout.Token);

        var status = await client.CallToolAsync(
            "workspace_status",
            new Dictionary<string, object?>(),
            cancellationToken: timeout.Token);
        Assert.True(status.IsError is not true, InProcessMcpFixture.TextOf(status));
        Assert.Equal("idle", InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(status).Phase);

        var list = await client.CallToolAsync(
            "workspace_list_projects",
            new Dictionary<string, object?>(),
            cancellationToken: timeout.Token);
        Assert.True(list.IsError is true, InProcessMcpFixture.TextOf(list));
        var error = InProcessMcpFixture.Deserialize<PolicyErrorDto>(list);
        Assert.Equal(PolicyErrorCodes.WorkspaceNotReady, error.Error);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.False(string.IsNullOrWhiteSpace(error.SuggestedAction));
    }

    private static async Task<McpClient> ConnectAsync(string roots, CancellationToken cancellationToken)
    {
        var serverDll = Path.Combine(AppContext.BaseDirectory, "DotNetMcp.Server.dll");
        Assert.True(File.Exists(serverDll), "Server assembly was not copied next to the tests.");

        return await McpClient.CreateAsync(
            new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "dotnet-mcp",
                Command = "dotnet",
                Arguments = [serverDll, "--roots", roots],
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["DOTNET_MCP_TRUSTED_ROOTS"] = null,
                },
            }),
            cancellationToken: cancellationToken);
    }
}