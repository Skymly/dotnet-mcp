using System.Text.RegularExpressions;
using DotNetMcp.Core;

namespace DotNetMcp.Tests;

public class FsharpBesideWorkspaceSessionSeamTests
{
    internal static readonly Regex ForbiddenRoslynAccess = new(
        @"\b\w+\s*!?\??\s*\.\s*Solution\b|GetCompilationAsync",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    [Fact]
    public void fsharp_adapter_does_not_read_roslyn_solution_or_compilation()
    {
        var fsharpDir = FindSrcDir("DotNetMcp.FSharp");
        var files = Directory.GetFiles(fsharpDir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedTree(path))
            .ToArray();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.False(
                ForbiddenRoslynAccess.IsMatch(text),
                $"{Path.GetRelativePath(fsharpDir, file)} reads a Roslyn solution or compilation.");
        }

        var snapshotPath = Path.Combine(FindSrcDir("DotNetMcp.Core"), "FSharpWorkspaceSnapshot.cs");
        var snapshot = File.ReadAllText(snapshotPath);
        Assert.DoesNotContain("Solution", snapshot, StringComparison.Ordinal);
        Assert.Null(typeof(FSharpWorkspaceSnapshot).GetProperty("Solution"));
        Assert.DoesNotContain(
            typeof(FSharpWorkspaceSnapshot).GetMethods().Select(m => m.Name),
            name => name.Contains("GetCompilationAsync", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("session.Solution")]
    [InlineData("Session.Solution")]
    [InlineData("sess.Solution")]
    [InlineData("session!.Solution")]
    [InlineData("session?.Solution")]
    [InlineData("GetCompilationAsync")]
    public void forbidden_roslyn_access_pattern_matches_aliases(string sample)
    {
        Assert.Matches(ForbiddenRoslynAccess, sample);
    }

    private static bool IsGeneratedTree(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(part =>
            part.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("bin", StringComparison.OrdinalIgnoreCase));
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