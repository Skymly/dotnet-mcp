namespace DotNetMcp.Tests;

public class ListToolsSeamTests
{
    [Fact]
    public async Task mcp_client_can_list_workspace_open_tool()
    {
        await using var fx = new InProcessMcpFixture();
        var tools = await fx.Client.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToArray();

        Assert.Contains("workspace_open", names);
    }

    [Fact]
    public async Task workspace_open_description_mentions_open_means_execute()
    {
        await using var fx = new InProcessMcpFixture();
        var tools = await fx.Client.ListToolsAsync();
        var open = Assert.Single(tools, t => t.Name == "workspace_open");

        Assert.Contains("MSBuild", open.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("generator", open.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("untrusted", open.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trusted root", open.Description, StringComparison.OrdinalIgnoreCase);
    }
}
