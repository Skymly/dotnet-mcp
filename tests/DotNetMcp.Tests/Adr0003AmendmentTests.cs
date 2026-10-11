namespace DotNetMcp.Tests;

public class Adr0003AmendmentTests
{
    [Fact]
    public void amendment_3_narrows_the_budget_tool_list_after_the_accepted_section()
    {
        var root = FindRepoRoot();
        var adr = File.ReadAllText(Path.Combine(root, "docs", "adr", "0003-long-running-operations-session-concurrency.md"));
        var decision = adr.IndexOf("## 决策", StringComparison.Ordinal);
        var amendment = adr.IndexOf("## Amendment 3", StringComparison.Ordinal);
        Assert.True(decision >= 0 && amendment > decision);
        var accepted = adr[decision..amendment];
        Assert.Contains("所有工具遵守软性时间预算", accepted, StringComparison.Ordinal);
        Assert.Contains("所有列表型工具必须实现部分结果 + 游标", accepted, StringComparison.Ordinal);
        var added = adr[amendment..];
        Assert.Contains("2026-10-11", added, StringComparison.Ordinal);
        Assert.Contains("project_list_generated_sources", added, StringComparison.Ordinal);
        Assert.Contains("symbol_find_implementations", added, StringComparison.Ordinal);
        Assert.Contains("symbol_find_references", added, StringComparison.Ordinal);
        Assert.Contains("xaml_diagnostics", added, StringComparison.Ordinal);
    }

    [Fact]
    public void readme_soft_budget_paragraph_names_the_unbudgeted_tools()
    {
        var root = FindRepoRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        Assert.DoesNotContain("List/scan tools honor a soft time budget", readme, StringComparison.Ordinal);
        Assert.Contains("project_list_generated_sources", readme, StringComparison.Ordinal);
        Assert.Contains("project_list_generator_diagnostics", readme, StringComparison.Ordinal);
        Assert.Contains("symbol_find_implementations", readme, StringComparison.Ordinal);
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
            if (File.Exists(Path.Combine(candidate, "DotNetMcp.slnx")))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root from the test host.");
    }
}
