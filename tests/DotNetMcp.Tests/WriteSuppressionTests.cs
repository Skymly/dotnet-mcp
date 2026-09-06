using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class WriteSuppressionTests
{
    [Fact]
    public void is_suppressed_uses_os_path_comparison()
    {
        var root = CreateTempDir("sup");
        var upper = Path.Combine(root, "Foo.cs");
        File.WriteAllText(upper, "x");
        var lower = Path.Combine(root, "foo.cs");
        var suppression = new WriteSuppression();

        try
        {
            using (suppression.Suppress(upper))
            {
                Assert.True(suppression.IsSuppressed(upper));
                if (OperatingSystem.IsWindows())
                {
                    Assert.True(suppression.IsSuppressed(lower));
                }
                else
                {
                    Assert.False(suppression.IsSuppressed(lower));
                }
            }

            Assert.False(suppression.IsSuppressed(upper));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnet-mcp-ws-{label}-{Guid.NewGuid():N}");
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
