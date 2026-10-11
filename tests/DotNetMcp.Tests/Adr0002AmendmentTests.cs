namespace DotNetMcp.Tests;

public class Adr0002AmendmentTests
{
    [Fact]
    public void amendment_5_names_the_disk_interface_and_keeps_the_accepted_block()
    {
        var root = FindRepoRoot();
        var adr = File.ReadAllText(Path.Combine(root, "docs", "adr", "0002-workspace-layer-pull-based-with-internal-fsw.md"));
        Assert.Contains("2026-10-11 Amendment 5", adr, StringComparison.Ordinal);
        var decision = adr.IndexOf("## 决策", StringComparison.Ordinal);
        var amendment = adr.IndexOf("## Amendment 5", StringComparison.Ordinal);
        Assert.True(decision >= 0 && amendment > decision);
        var accepted = adr[decision..amendment];
        Assert.Contains("interface IWorkspaceProvider", accepted, StringComparison.Ordinal);
        Assert.Contains("GeneratorDriverRunResult", accepted, StringComparison.Ordinal);
        var added = adr[amendment..];
        Assert.Contains("DriverRunSnapshot", added, StringComparison.Ordinal);
        Assert.Contains("FSharpSnapshot", added, StringComparison.Ordinal);
        Assert.DoesNotContain("interface IWorkspaceProvider", added, StringComparison.Ordinal);
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