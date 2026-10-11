using System.Text.RegularExpressions;
namespace DotNetMcp.Tests;

public class ReadmeInstallSeamTests
{
    [Fact]
    public void quick_start_leads_with_source_run_not_unpublished_dnx()
    {
        var readme = File.ReadAllText(Path.Combine(FindRepoRoot(), "README.md"));
        var start = readme.IndexOf("## Quick Start", StringComparison.Ordinal);
        Assert.True(start >= 0, "README.md is missing ## Quick Start.");
        var rest = readme[start..];

        var bash = IndexOfFence(rest, "```bash");
        var json = IndexOfFence(rest, "```json");
        Assert.True(bash >= 0, "Quick Start has no bash fence.");
        Assert.True(json >= 0, "Quick Start has no mcpServers json fence.");

        var firstBash = FenceBody(rest, bash);
        Assert.DoesNotContain("dnx Skymly.DotNetMcp", firstBash, StringComparison.Ordinal);

        var firstJson = FenceBody(rest, json);
        Assert.DoesNotContain("\"command\": \"dnx\"", firstJson, StringComparison.Ordinal);

        Assert.Contains("dnx Skymly.DotNetMcp", rest, StringComparison.Ordinal);
        Assert.Contains("after the package is published", rest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void batch_diagnostics_budget_is_not_marked_reserved()
    {
        var readme = File.ReadAllText(Path.Combine(FindRepoRoot(), "README.md"));
        var line = readme.Split('\n').First(l => l.Contains("DOTNET_MCP_BUDGET_BATCH_DIAGNOSTICS_MS", StringComparison.Ordinal));
        Assert.DoesNotContain("Reserved", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("projectId", line, StringComparison.Ordinal);
        Assert.Contains("omit", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void readme_does_not_call_the_working_directory_a_sandbox()
    {
        var readme = File.ReadAllText(Path.Combine(FindRepoRoot(), "README.md"));
        Assert.DoesNotContain("implicit sandbox", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never an implicit trusted root", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void server_json_does_not_advertise_unpublished_nuget_dnx_package()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "DotNetMcp.Server", ".mcp", "server.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var nugetOrDnx = false;
        if (root.TryGetProperty("packages", out var packages) && packages.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var package in packages.EnumerateArray())
            {
                var registry = package.TryGetProperty("registryType", out var registryType)
                    ? registryType.GetString()
                    : null;
                var hint = package.TryGetProperty("runtimeHint", out var runtimeHint)
                    ? runtimeHint.GetString()
                    : null;
                if (string.Equals(registry, "nuget", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(hint, "dnx", StringComparison.OrdinalIgnoreCase))
                {
                    nugetOrDnx = true;
                }
            }
        }

        Assert.False(
            nugetOrDnx,
            "server.json must not advertise a NuGet/dnx package while Skymly.DotNetMcp is unpublished.");
    }

    [Fact]
    public void install_and_ci_commands_reference_existing_paths()
    {
        var root = FindRepoRoot();
        var english = English(File.ReadAllText(Path.Combine(root, "README.md")));

        var quick = Section(english, "## Quick Start", "## MCP tools");
        var commands = BashCommands(quick).ToArray();
        Assert.Contains(commands, c => c.StartsWith("dotnet run --project src/DotNetMcp.Server", StringComparison.Ordinal));
        Assert.Contains(commands, c => c.StartsWith("dotnet pack src/DotNetMcp.Server", StringComparison.Ordinal));
        Assert.True(Directory.Exists(Path.Combine(root, "src", "DotNetMcp.Server")));

        var json = FenceBody(quick, IndexOfFence(quick, "```json"));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("dotnet-mcp");
        Assert.Equal("dotnet", server.GetProperty("command").GetString());
        var args = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray();
        Assert.Equal(new[] { "run", "--project", "src/DotNetMcp.Server", "--", "--roots", "/path/to/repo" }, args);

        var ci = Section(english, "## Development / CI", null);
        var ciCommands = BashCommands(ci).ToArray();
        Assert.Contains(ciCommands, c => c.Contains("DotNetMcp.slnx", StringComparison.Ordinal));
        Assert.Contains(ciCommands, c => c.Contains("benches/DotNetMcp.Bench", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(root, "DotNetMcp.slnx")));
        Assert.True(Directory.Exists(Path.Combine(root, "benches", "DotNetMcp.Bench")));
    }

    [Fact]
    public void readme_tables_and_agent_loop_are_structured()
    {
        var english = English(File.ReadAllText(Path.Combine(FindRepoRoot(), "README.md")));
        var tables = MarkdownTables(english);
        Assert.Equal(2, tables.Count);

        var tools = tables[0];
        Assert.Equal(new[] { "Group", "Tools" }, tools[0]);
        Assert.Equal(
            new[] { "Workspace", "Diagnostic fix", "Symbol", "Project", "XAML" },
            tools.Skip(1).Select(row => row[0]).ToArray());
        var toolNames = tools.Skip(1)
            .SelectMany(row => Regex.Matches(row[1], "`([^`]+)`").Select(m => m.Groups[1].Value))
            .ToArray();
        Assert.Equal(31, toolNames.Length);
        Assert.Contains("workspace_open", toolNames);
        Assert.Contains("symbol_apply_refactoring", toolNames);
        Assert.Contains("xaml_diagnostics", toolNames);

        var budgets = tables[1];
        Assert.Equal(new[] { "Environment variable", "Default", "Use" }, budgets[0]);
        Assert.Equal(5, budgets.Length - 1);
        foreach (var row in budgets.Skip(1))
        {
            Assert.Matches("^`DOTNET_MCP_BUDGET_[A-Z0-9_]+`$", row[0]);
            Assert.True(int.TryParse(row[1], out var ms) && ms > 0, row[1]);
        }

        var loop = english.Split('\n').First(l => l.StartsWith("Typical agent loop:", StringComparison.Ordinal));
        var names = Regex.Matches(loop, "`([a-z_]+)`").Select(m => m.Groups[1].Value).Where(n => n.Contains('_')).ToArray();
        Assert.Equal(new[] { "workspace_open", "workspace_status", "symbol_resolve" }, names);
    }
    [Fact]
    public void readme_test_command_lock_note_and_chinese_pack_match_english()
    {
        var readme = File.ReadAllText(Path.Combine(FindRepoRoot(), "README.md"));
        var english = English(readme);
        var ci = Section(english, "## Development / CI", null);
        Assert.Contains("dotnet test DotNetMcp.slnx -c Release --no-build --verbosity normal", ci, StringComparison.Ordinal);
        Assert.Contains("packages.lock.json", ci, StringComparison.Ordinal);
        Assert.Contains("Transitive dependency resolution is not locked", ci, StringComparison.Ordinal);

        var zh = readme[(readme.IndexOf("## 中文", StringComparison.Ordinal))..];
        Assert.Contains("dotnet pack src/DotNetMcp.Server -c Release -o ./artifacts", zh, StringComparison.Ordinal);
        Assert.Contains("dotnet tool exec --source ./artifacts --yes Skymly.DotNetMcp -- --roots /path/to/repo", zh, StringComparison.Ordinal);
    }

    private static string English(string readme)
    {
        var zh = readme.IndexOf("## 中文", StringComparison.Ordinal);
        return zh < 0 ? readme : readme[..zh];
    }

    private static string Section(string text, string startHeading, string? endHeading)
    {
        var start = text.IndexOf(startHeading, StringComparison.Ordinal);
        Assert.True(start >= 0, startHeading);
        if (endHeading is null)
        {
            return text[start..];
        }

        var end = text.IndexOf(endHeading, start + startHeading.Length, StringComparison.Ordinal);
        Assert.True(end > start, endHeading);
        return text[start..end];
    }

    private static IEnumerable<string> BashCommands(string text)
    {
        var search = 0;
        while (search < text.Length)
        {
            var relative = IndexOfFence(text[search..], "```bash");
            if (relative < 0)
            {
                yield break;
            }

            var fence = search + relative;
            foreach (var line in FenceBody(text, fence).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!line.StartsWith('#'))
                {
                    yield return line;
                }
            }

            search = fence + 7;
        }
    }

    private static List<string[][]> MarkdownTables(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var tables = new List<string[][]>();
        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (!lines[i].StartsWith('|') || !lines[i + 1].StartsWith('|') || !lines[i + 1].Contains("---", StringComparison.Ordinal))
            {
                continue;
            }

            var rows = new List<string[]> { Cells(lines[i]) };
            for (var j = i + 2; j < lines.Length && lines[j].StartsWith('|'); j++)
            {
                rows.Add(Cells(lines[j]));
            }

            tables.Add(rows.ToArray());
            i += rows.Count;
        }

        return tables;
    }

    private static string[] Cells(string row) =>
        row.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
    private static int IndexOfFence(string text, string fence) =>
        text.IndexOf(fence + Environment.NewLine, StringComparison.Ordinal) >= 0
            ? text.IndexOf(fence + Environment.NewLine, StringComparison.Ordinal)
            : text.IndexOf(fence + "\n", StringComparison.Ordinal);

    private static string FenceBody(string text, int fenceStart)
    {
        var nl = text.IndexOf('\n', fenceStart);
        var end = text.IndexOf("```", nl + 1, StringComparison.Ordinal);
        return text[(nl + 1)..end];
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
