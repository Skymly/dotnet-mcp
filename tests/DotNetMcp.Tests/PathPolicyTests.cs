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

    [Fact]
    public void trusted_roots_contains_empty_or_illegal_path_is_false_not_throw()
    {
        var root = CreateTempDir("empty");
        try
        {
            var trusted = TrustedRoots.Create([root]);
            Assert.False(trusted.Contains(""));
            Assert.False(trusted.Contains("   "));
            Assert.False(trusted.Contains("not-a-path|<>"));
            Assert.True(trusted.Contains(root));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void unc_path_is_not_under_local_trusted_root()
    {
        var root = CreateTempDir("unc");
        try
        {
            var trusted = TrustedRoots.Create([root]);
            Assert.False(trusted.Contains(@"\\server\share\secret.cs"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void extended_prefix_path_matches_same_local_root_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDir("ext");
        var inside = Path.Combine(root, "a.txt");
        File.WriteAllText(inside, "x");

        try
        {
            var full = Path.GetFullPath(inside);
            var extended = @"\\?\" + full;
            Assert.Equal(PathPolicy.Normalize(full), PathPolicy.Normalize(extended));
            var trusted = TrustedRoots.Create([root]);
            Assert.True(trusted.Contains(extended));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void normalize_missing_child_still_stays_under_root()
    {
        var root = CreateTempDir("missing");
        try
        {
            var missing = Path.Combine(root, "no", "such", "file.cs");
            var normalized = PathPolicy.Normalize(missing);
            Assert.True(PathPolicy.IsUnderRoot(normalized, PathPolicy.Normalize(root)));
            var trusted = TrustedRoots.Create([root]);
            Assert.True(trusted.Contains(missing));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void windows_dot_dot_space_segment_does_not_escape_trusted_root()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDir("ddspace");
        var parent = Path.GetDirectoryName(root)!;
        var outside = Path.Combine(parent, $"evil-{Guid.NewGuid():N}.sln");
        var lexical = Path.Combine(root, ".. ", Path.GetFileName(outside));

        try
        {
            var nRoot = PathPolicy.Normalize(root);
            var normalized = PathPolicy.Normalize(lexical);
            Assert.True(PathPolicy.IsUnderRoot(normalized, nRoot));
            Assert.NotEqual(PathPolicy.Normalize(outside), normalized);

            var trusted = TrustedRoots.Create([root]);
            Assert.True(trusted.Contains(lexical));

            try
            {
                File.WriteAllText(lexical, "OUTSIDE");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Write may fail on the non-canonical segment; that is not an escape.
            }

            Assert.False(File.Exists(outside));
            if (File.Exists(lexical) || File.Exists(normalized))
            {
                var written = File.Exists(normalized) ? normalized : PathPolicy.Normalize(lexical);
                Assert.True(PathPolicy.IsUnderRoot(written, nRoot));
            }
        }
        finally
        {
            TryDelete(root);
            try
            {
                if (File.Exists(outside))
                {
                    File.Delete(outside);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void windows_trailing_dot_or_space_contains_does_not_escape_root()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDir("trail");
        var inside = Path.Combine(root, "a.txt");
        File.WriteAllText(inside, "x");
        var parent = Path.GetDirectoryName(root)!;
        var sibling = Path.Combine(parent, "b.txt");

        try
        {
            var trusted = TrustedRoots.Create([root]);
            Assert.True(trusted.Contains(inside));
            Assert.True(trusted.Contains(inside + "."));
            Assert.True(trusted.Contains(inside + " "));
            Assert.False(trusted.Contains(sibling));
            Assert.False(trusted.Contains(sibling + "."));
            Assert.False(trusted.Contains(sibling + " "));
        }
        finally
        {
            TryDelete(root);
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