namespace DotNetMcp.Tests;

/// <summary>
/// Snapshot / guard for the tool surface (ADR-0004 §3): read tools + explicit rename + Diagnostic fix + Code Refactoring.
/// </summary>
public class ToolSurfaceGuardTests
{
    /// <summary>
    /// Canonical allowlist. Update deliberately when adding tools.
    /// </summary>
    private static readonly string[] AllowedToolNames =
    [
        "diagnostics_apply_fix",
        "diagnostics_list_fixes",
        "diagnostics_preview_fix",
        "project_diagnostics",
        "project_list_generated_sources",
        "project_list_generator_diagnostics",
        "project_list_dynamic_invocations",
        "project_list_generators",
        "symbol_apply_refactoring",
        "symbol_apply_rename",
        "symbol_attribution",
        "symbol_find_callers",
        "symbol_find_implementations",
        "symbol_find_references",
        "symbol_goto_definition",
        "symbol_list_refactorings",
        "symbol_members",
        "symbol_preview_refactoring",
        "symbol_preview_rename",
        "symbol_resolve",
        "symbol_summary",
        "symbol_type_hierarchy",
        "workspace_check_drift",
        "workspace_list_projects",
        "workspace_open",
        "workspace_status",
        "xaml_diagnostics",
        "xaml_list_xmlns",
        "xaml_resolve_binding",
        "xaml_resolve_class",
        "xaml_resolve_name"
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
