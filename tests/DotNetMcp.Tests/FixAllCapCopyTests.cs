using DotNetMcp.Core;

namespace DotNetMcp.Tests;

public class FixAllCapCopyTests
{
    [Fact]
    public void fix_all_suggested_actions_do_not_point_at_a_missing_knob()
    {
        var source = File.ReadAllText(Path.Combine(FindCoreDir(), "DiagnosticFixService.cs"));
        Assert.DoesNotContain("raise the document Fix all cap", source, StringComparison.Ordinal);
        Assert.DoesNotContain("raise the host FixAllProjectMaxApplications cap", source, StringComparison.Ordinal);
        Assert.Contains("not configurable", source, StringComparison.Ordinal);
        Assert.Contains("not an environment variable", source, StringComparison.Ordinal);
        Assert.Contains("32", source, StringComparison.Ordinal);
    }

    private static string FindCoreDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "DotNetMcp.Core");
            if (File.Exists(Path.Combine(candidate, "DiagnosticFixService.cs")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate src/DotNetMcp.Core from the test assembly.");
    }
}