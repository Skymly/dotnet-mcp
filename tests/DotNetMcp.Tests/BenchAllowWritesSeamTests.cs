using DotNetMcp.Bench;

namespace DotNetMcp.Tests;

public class BenchAllowWritesSeamTests
{
    [Fact]
    public void help_and_docs_do_not_advertise_allow_writes()
    {
        Assert.DoesNotContain("--allow-writes", BenchOptions.Usage, StringComparison.Ordinal);
        Assert.DoesNotContain("apply_*", BenchOptions.Usage, StringComparison.Ordinal);

        var doc = File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "perf", "benchmark.md"));
        Assert.DoesNotContain("--allow-writes", doc, StringComparison.Ordinal);
        Assert.Contains("does not run apply", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void allow_writes_flag_is_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => BenchOptions.Parse(["--allow-writes"]));
        Assert.Contains("Unknown argument", ex.Message, StringComparison.Ordinal);
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