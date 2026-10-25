using System.ComponentModel;
using System.Reflection;
using DotNetMcp.Server;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotNetMcp.Tests;

/// <summary>
/// Agent guidance seam (#527): the initialize-result instructions, the two
/// workflow prompts, the docs snippet, and the 1-based wording locked onto every
/// position-named tool parameter.
/// </summary>
public class AgentGuidanceSeamTests
{
    [Fact]
    public async Task initialize_result_carries_the_standard_loop_instructions()
    {
        await using var fx = new InProcessMcpFixture(TrustedRoots.Create([Path.GetTempPath()]));

        var instructions = fx.Client.ServerInstructions;
        Assert.False(string.IsNullOrWhiteSpace(instructions));
        Assert.True(
            instructions.Length <= 1500,
            $"instructions land in every client's context; keep them under ~1500 chars, was {instructions.Length}.");

        foreach (var tool in new[]
        {
            "workspace_open",
            "workspace_status",
            "symbol_resolve",
            "symbol_resolve_at",
        })
        {
            Assert.Contains(tool, instructions, StringComparison.Ordinal);
        }

        Assert.Contains("1-based", instructions, StringComparison.Ordinal);
        Assert.Contains("preview", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MSBuild", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task prompts_list_is_exactly_the_two_workflow_prompts()
    {
        await using var fx = new InProcessMcpFixture(TrustedRoots.Create([Path.GetTempPath()]));

        var prompts = await fx.Client.ListPromptsAsync();
        var names = prompts.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "safe_rename", "trace_symbol" }, names);

        var trace = Assert.Single(prompts, p => p.Name == "trace_symbol");
        Assert.False(string.IsNullOrWhiteSpace(trace.Description));
        var traceArgs = trace.ProtocolPrompt.Arguments ?? [];
        Assert.Equal(
            new[] { "column", "line", "name", "path" },
            traceArgs.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.All(traceArgs, a => Assert.True(a.Required is not true, $"{a.Name} must be optional"));
        Assert.All(traceArgs, a => Assert.False(string.IsNullOrWhiteSpace(a.Description), $"{a.Name} needs a description"));
        Assert.Equal("1-based", PositionMark(traceArgs, "line"));
        Assert.Equal("1-based", PositionMark(traceArgs, "column"));

        var rename = Assert.Single(prompts, p => p.Name == "safe_rename");
        Assert.False(string.IsNullOrWhiteSpace(rename.Description));
        var renameArgs = rename.ProtocolPrompt.Arguments ?? [];
        Assert.Equal(
            new[] { "newName", "symbol" },
            renameArgs.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.All(renameArgs, a => Assert.True(a.Required is true, $"{a.Name} must be required"));

        static string? PositionMark(IList<PromptArgument> args, string name) =>
            args.Single(a => a.Name == name).Description is { } d && d.Contains("1-based", StringComparison.Ordinal)
                ? "1-based"
                : null;
    }

    [Fact]
    public async Task trace_symbol_renders_the_name_entry()
    {
        await using var fx = new InProcessMcpFixture(TrustedRoots.Create([Path.GetTempPath()]));

        var result = await fx.Client.GetPromptAsync(
            "trace_symbol",
            new Dictionary<string, object?> { ["name"] = "SampleLib.Calculator" });
        var text = RenderedText(result);

        Assert.Contains("symbol_resolve", text, StringComparison.Ordinal);
        Assert.Contains("SampleLib.Calculator", text, StringComparison.Ordinal);
        Assert.Contains("symbol_summary", text, StringComparison.Ordinal);
        Assert.Contains("symbol_attribution", text, StringComparison.Ordinal);
        Assert.Contains("triggers[]", text, StringComparison.Ordinal);
        Assert.Contains("symbol_find_references", text, StringComparison.Ordinal);
        Assert.Contains("xamlUsage", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task trace_symbol_renders_the_position_entry()
    {
        await using var fx = new InProcessMcpFixture(TrustedRoots.Create([Path.GetTempPath()]));

        var result = await fx.Client.GetPromptAsync(
            "trace_symbol",
            new Dictionary<string, object?>
            {
                ["path"] = "C:/repo/src/Widget.cs",
                ["line"] = 12,
                ["column"] = 34,
            });
        var text = RenderedText(result);

        Assert.Contains("symbol_resolve_at", text, StringComparison.Ordinal);
        Assert.Contains("C:/repo/src/Widget.cs", text, StringComparison.Ordinal);
        Assert.Contains("line = 12", text, StringComparison.Ordinal);
        Assert.Contains("column = 34", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task safe_rename_renders_preview_review_apply_recheck()
    {
        await using var fx = new InProcessMcpFixture(TrustedRoots.Create([Path.GetTempPath()]));

        var result = await fx.Client.GetPromptAsync(
            "safe_rename",
            new Dictionary<string, object?>
            {
                ["symbol"] = "SampleLib.Calculator",
                ["newName"] = "Arithmetic",
            });
        var text = RenderedText(result);

        Assert.Contains("SampleLib.Calculator", text, StringComparison.Ordinal);
        Assert.Contains("Arithmetic", text, StringComparison.Ordinal);
        Assert.Contains("symbol_preview_rename", text, StringComparison.Ordinal);
        Assert.Contains("unverifiedXamlMentions", text, StringComparison.Ordinal);
        Assert.Contains("symbol_apply_rename", text, StringComparison.Ordinal);
        Assert.Contains("project_diagnostics", text, StringComparison.Ordinal);
    }

    [Fact]
    public void every_position_named_tool_parameter_says_1_based()
    {
        var positionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "line", "column", "startline", "startcolumn", "endline", "endcolumn",
        };

        var checkedParameters = 0;
        var toolTypes = typeof(ServerHost).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null);
        foreach (var method in toolTypes.SelectMany(t => t.GetMethods(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)))
        {
            var tool = method.GetCustomAttribute<McpServerToolAttribute>();
            if (tool is null)
            {
                continue;
            }

            var parameters = method.GetParameters()
                .Where(p => p.Name is not null && positionNames.Contains(p.Name))
                .ToArray();
            if (parameters.Length == 0)
            {
                continue;
            }

            var toolDescription = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
            Assert.True(
                toolDescription.Contains("1-based", StringComparison.OrdinalIgnoreCase),
                $"tool '{tool.Name}' takes positions but its description never says 1-based.");

            foreach (var parameter in parameters)
            {
                var description = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
                Assert.True(
                    description.Contains("1-based", StringComparison.OrdinalIgnoreCase),
                    $"tool '{tool.Name}' parameter '{parameter.Name}' description never says 1-based.");
                checkedParameters++;
            }
        }

        Assert.True(
            checkedParameters >= 10,
            $"guard must bite — expected at least the resolve_at + fix-locator position parameters, saw {checkedParameters}.");
    }

    [Fact]
    public void agent_guidance_snippet_exists_and_is_linked_from_the_readme()
    {
        var root = FindRepoRoot();
        var snippetPath = Path.Combine(root, "docs", "agent-guidance.md");
        Assert.True(File.Exists(snippetPath), "docs/agent-guidance.md is missing.");

        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        Assert.Contains("docs/agent-guidance.md", readme, StringComparison.Ordinal);

        var snippet = File.ReadAllText(snippetPath);
        Assert.Contains("```markdown", snippet, StringComparison.Ordinal);
        foreach (var term in new[]
        {
            "workspace_open",
            "workspace_status",
            "symbol_resolve",
            "symbol_resolve_at",
            "1-based",
            "symbol_preview_rename",
            "symbol_apply_rename",
            "unverifiedXamlMentions",
        })
        {
            Assert.Contains(term, snippet, StringComparison.Ordinal);
        }
    }

    private static string RenderedText(GetPromptResult result)
    {
        var message = Assert.Single(result.Messages);
        var content = Assert.IsType<TextContentBlock>(message.Content);
        Assert.Equal(Role.User, message.Role);
        Assert.False(string.IsNullOrWhiteSpace(content.Text));
        return content.Text!;
    }

    private static string FindRepoRoot()
    {
        var candidates = new[]
        {
            Environment.CurrentDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "README.md")))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not locate README.md from the test host.");
    }
}
