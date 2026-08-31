using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class PathPolicyTests
{
    [Fact]
    public void normalize_then_is_under_root_true_for_child_false_for_sibling()
    {
        var root = CreateTempDir("root");
        var childDir = Path.Combine(root, "child");
        Directory.CreateDirectory(childDir);
        var child = Path.Combine(childDir, "file.txt");
        File.WriteAllText(child, "x");
        var sibling = CreateTempDir("sib");

        try
        {
            var nRoot = PathPolicy.Normalize(root);
            Assert.True(PathPolicy.IsUnderRoot(PathPolicy.Normalize(child), nRoot));
            Assert.True(PathPolicy.IsUnderRoot(nRoot, nRoot));
            Assert.False(PathPolicy.IsUnderRoot(PathPolicy.Normalize(sibling), nRoot));
        }
        finally
        {
            TryDelete(root);
            TryDelete(sibling);
        }
    }

    [Fact]
    public void trusted_roots_contains_listed_root_not_outside()
    {
        var root = CreateTempDir("trust");
        var inside = Path.Combine(root, "a.txt");
        File.WriteAllText(inside, "x");
        var outside = CreateTempDir("out");

        try
        {
            var trusted = TrustedRoots.Create([root]);
            Assert.True(trusted.Contains(inside));
            Assert.True(trusted.Contains(root));
            Assert.False(trusted.Contains(outside));
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    private static string CreateTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnet-mcp-pp-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}