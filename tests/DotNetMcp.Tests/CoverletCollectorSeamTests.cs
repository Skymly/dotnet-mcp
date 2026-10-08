namespace DotNetMcp.Tests;

public class CoverletCollectorSeamTests
{
    [Fact]
    public void product_tests_do_not_reference_unused_coverlet_collector()
    {
        var root = FindRepoRoot();
        var csproj = File.ReadAllText(Path.Combine(root, "tests", "DotNetMcp.Tests", "DotNetMcp.Tests.csproj"));
        Assert.DoesNotContain("coverlet", csproj, StringComparison.OrdinalIgnoreCase);
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
