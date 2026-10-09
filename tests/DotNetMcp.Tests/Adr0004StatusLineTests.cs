namespace DotNetMcp.Tests;

public class Adr0004StatusLineTests
{
    [Fact]
    public void status_line_lists_amendments_2_and_3_and_current_write_surface()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "adr", "0004-security-and-path-policy.md"));
        var status = text.Split('\n').First(line => line.StartsWith("Accepted", StringComparison.Ordinal));
        Assert.Contains("Amendment 2", status, StringComparison.Ordinal);
        Assert.Contains("Amendment 3", status, StringComparison.Ordinal);

        var amendment = text[(text.LastIndexOf("## Amendment 7", StringComparison.Ordinal))..];
        foreach (var tool in new[]
        {
            "symbol_preview_rename",
            "symbol_apply_rename",
            "diagnostics_list_fixes",
            "diagnostics_preview_fix",
            "diagnostics_apply_fix",
            "symbol_list_refactorings",
            "symbol_preview_refactoring",
            "symbol_apply_refactoring",
            "workspace_check_drift",
        })
        {
            Assert.Contains(tool, amendment, StringComparison.Ordinal);
        }

        Assert.Contains("允许名单只增加 rename 两步（本票只加 preview）", text, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "adr", "0004-security-and-path-policy.md")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate ADR-0004.");
    }
}