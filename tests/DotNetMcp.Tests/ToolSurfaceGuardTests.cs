namespace DotNetMcp.Tests;

/// <summary>
/// Snapshot / guard for the read-only tool surface (ADR-0004 §3).
/// </summary>
public class ToolSurfaceGuardTests
{
    /// <summary>
    /// Canonical allowlist for v0 read-only skeleton. Update deliberately when adding tools.
    /// </summary>
    private static readonly string[] AllowedToolNames =
    [
        "project_diagnostics",
        "project_list_generators",
        "symbol_find_references",
        "symbol_goto_definition",
        "symbol_members",
        "symbol_resolve",
        "symbol_summary",
        "workspace_list_projects",
        "workspace_open",
        "workspace_status"
    ];

    private static readonly string[] ForbiddenNameFragments =
    [
        "write",
        "delete",
        "create_file",
        "apply_edit",
        "patch_file",
        "shell",
        "exec",
        "run_command",
        "http",
        "fetch",
        "download",
        "upload",
        "network"
    ];

    [Fact]
    public async Task tool_surface_matches_readonly_allowlist_snapshot()
    {
        await using var fx = new InProcessMcpFixture();
        var tools = await fx.Client.ListToolsAsync();
        var names = tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(AllowedToolNames.OrderBy(n => n, StringComparer.Ordinal), names);
    }

    [Fact]
    public async Task tool_surface_has_no_write_command_or_network_tools()
    {
        await using var fx = new InProcessMcpFixture();
        var tools = await fx.Client.ListToolsAsync();

        foreach (var tool in tools)
        {
            foreach (var fragment in ForbiddenNameFragments)
            {
                Assert.DoesNotContain(fragment, tool.Name, StringComparison.OrdinalIgnoreCase);
            }

            var description = tool.Description ?? string.Empty;
            Assert.DoesNotContain("write file", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("execute command", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("http request", description, StringComparison.OrdinalIgnoreCase);
        }
    }
}
