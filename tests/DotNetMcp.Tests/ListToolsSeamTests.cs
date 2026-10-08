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

    [Fact]
    public async Task symbol_resolve_description_covers_csharp_vb_and_fsharp()
    {
        await using var fx = new InProcessMcpFixture();
        var tools = await fx.Client.ListToolsAsync();
        var resolve = Assert.Single(tools, t => t.Name == "symbol_resolve");

        Assert.DoesNotContain("Resolve a C# symbol", resolve.Description, StringComparison.Ordinal);
        Assert.Contains("C#", resolve.Description, StringComparison.Ordinal);
        Assert.Contains("VB", resolve.Description, StringComparison.Ordinal);
        Assert.Contains("F#", resolve.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("Roslyn projectId", resolve.Description, StringComparison.OrdinalIgnoreCase);

        var attribution = Assert.Single(tools, t => t.Name == "symbol_attribution");
        Assert.Contains("F#", attribution.Description, StringComparison.Ordinal);
        Assert.Contains("GeneratorLanguageNotSupported", attribution.Description, StringComparison.Ordinal);
        Assert.Contains("C#", attribution.Description, StringComparison.Ordinal);
        Assert.Contains("VB", attribution.Description, StringComparison.Ordinal);

        var symbolTools = File.ReadAllText(Path.Combine(FindServerDir(), "SymbolTools.cs"));
        Assert.DoesNotContain("Optional Roslyn projectId", symbolTools, StringComparison.Ordinal);
        Assert.Contains("Optional projectId GUID string from workspace_list_projects", symbolTools, StringComparison.Ordinal);
    }

    [Fact]
    public void mcp_server_tools_declare_annotations()
    {
        var serverDir = FindServerDir();
        var apply = new HashSet<string>(StringComparer.Ordinal)
        {
            "diagnostics_apply_fix",
            "symbol_apply_rename",
            "symbol_apply_refactoring",
        };
        var names = new List<string>();
        foreach (var file in Directory.GetFiles(serverDir, "*Tools.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                         text,
                         @"McpServerTool\(Name = ""([^""]+)""(?<rest>[^)]*)\)"))
            {
                var name = match.Groups[1].Value;
                var rest = match.Groups["rest"].Value;
                names.Add(name);
                Assert.Contains("OpenWorld = false", rest, StringComparison.Ordinal);
                Assert.Contains(ExpectReadOnly(name) ? "ReadOnly = true" : "ReadOnly = false", rest, StringComparison.Ordinal);
                Assert.Contains(ExpectDestructive(name) ? "Destructive = true" : "Destructive = false", rest, StringComparison.Ordinal);
                Assert.Contains(ExpectIdempotent(name) ? "Idempotent = true" : "Idempotent = false", rest, StringComparison.Ordinal);
            }
        }

        Assert.Equal(31, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("symbol_preview_rename", names);
        Assert.Contains("diagnostics_apply_fix", names);
    }

    [Fact]
    public async Task listed_tools_expose_all_four_annotations()
    {
        await using var fx = new InProcessMcpFixture();
        var tools = await fx.Client.ListToolsAsync();
        Assert.Equal(31, tools.Count);

        foreach (var tool in tools)
        {
            var annotations = tool.ProtocolTool.Annotations;
            Assert.NotNull(annotations);
            Assert.Equal(false, annotations!.OpenWorldHint);
            Assert.Equal(ExpectReadOnly(tool.Name), annotations.ReadOnlyHint);
            Assert.Equal(ExpectDestructive(tool.Name), annotations.DestructiveHint);
            Assert.Equal(ExpectIdempotent(tool.Name), annotations.IdempotentHint);
        }
    }

    private static bool ExpectDestructive(string name) =>
        name is "diagnostics_apply_fix" or "symbol_apply_rename" or "symbol_apply_refactoring";

    private static bool ExpectReadOnly(string name) =>
        !ExpectDestructive(name) && name is not ("workspace_open" or "workspace_check_drift");

    private static bool ExpectIdempotent(string name) => ExpectReadOnly(name);

    private static string FindServerDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "DotNetMcp.Server");
            if (File.Exists(Path.Combine(candidate, "WorkspaceTools.cs")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate src/DotNetMcp.Server.");
    }
}
