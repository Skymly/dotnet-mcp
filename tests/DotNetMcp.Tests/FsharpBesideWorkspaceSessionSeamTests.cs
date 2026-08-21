using DotNetMcp.Core;

namespace DotNetMcp.Tests;

public class FsharpBesideWorkspaceSessionSeamTests
{
    [Fact]
    public void fsharp_adapter_does_not_read_roslyn_solution_or_compilation()
    {
        var fsharpDir = FindSrcDir("DotNetMcp.FSharp");
        foreach (var file in Directory.GetFiles(fsharpDir, "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("session.Solution", text, StringComparison.Ordinal);
            Assert.DoesNotContain("GetCompilationAsync", text, StringComparison.Ordinal);
        }

        var snapshotPath = Path.Combine(FindSrcDir("DotNetMcp.Core"), "FSharpWorkspaceSnapshot.cs");
        var snapshot = File.ReadAllText(snapshotPath);
        Assert.DoesNotContain("Solution", snapshot, StringComparison.Ordinal);
        Assert.Null(typeof(FSharpWorkspaceSnapshot).GetProperty("Solution"));
        Assert.DoesNotContain(
            typeof(FSharpWorkspaceSnapshot).GetMethods().Select(m => m.Name),
            name => name.Contains("GetCompilationAsync", StringComparison.Ordinal));
    }

    private static string FindSrcDir(string project)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", project);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException($"Could not locate src/{project} from the test assembly.");
    }
}
