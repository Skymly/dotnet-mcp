namespace DotNetMcp.Tests;

public class Adr0006SeamTests
{
    [Fact]
    public void adr_0006_is_accepted_and_keeps_xaml_off_the_adapter_seam()
    {
        var path = Path.Combine(FindRepoRoot(), "docs", "adr", "0006-xaml-usage-index-core-seam.md");
        var text = File.ReadAllText(path);
        var status = text.Split('\n').First(line => line.StartsWith("Accepted", StringComparison.Ordinal));
        Assert.Contains("2026-10-25", status, StringComparison.Ordinal);

        Assert.Contains("IXamlUsageSource", text, StringComparison.Ordinal);
        Assert.Contains("ILanguageAdapter", text, StringComparison.Ordinal);
        Assert.Contains("(Epoch, ProjectId)", text, StringComparison.Ordinal);
        Assert.Contains("IWorkspaceSessionCaches", text, StringComparison.Ordinal);
        // The consumers landed in later chunks — the ADR names both and keeps
        // the unwired-behavior invariant.
        Assert.Contains("symbol_find_references", text, StringComparison.Ordinal);
        Assert.Contains("symbol_resolve_at", text, StringComparison.Ordinal);
        Assert.Contains("未接线", text, StringComparison.Ordinal);
    }

    [Fact]
    public void adr_0001_points_at_adr_0006_in_amendment_8()
    {
        var adr = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "docs", "adr", "0001-core-interface-llm-first-symbol-handles.md"));
        var status = adr.Split('\n').First(line => line.StartsWith("Accepted", StringComparison.Ordinal));
        Assert.Contains("2026-10-25 Amendment 8", status, StringComparison.Ordinal);

        var amendment = adr[adr.LastIndexOf("## Amendment 8", StringComparison.Ordinal)..];
        Assert.Contains("0006-xaml-usage-index-core-seam.md", amendment, StringComparison.Ordinal);
        Assert.Contains("IXamlUsageSource", amendment, StringComparison.Ordinal);
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
            if (File.Exists(Path.Combine(candidate, "docs", "adr", "0006-xaml-usage-index-core-seam.md")))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root from the test host.");
    }
}
