using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class ToolchainRootsTests
{
    [Fact]
    public void discover_includes_nuget_packages_from_the_environment()
    {
        var packages = CreateTempDir("pkgs");
        var previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        try
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", packages);
            var roots = ToolchainRoots.Discover();
            Assert.True(roots.Contains(packages));
            Assert.True(roots.Contains(Path.Combine(packages, "example.pkg", "analyzers", "dotnet", "cs", "Example.dll")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", previous);
            TryDelete(packages);
        }
    }

    [Fact]
    public void install_root_walk_stops_at_a_directory_with_sdk_and_shared()
    {
        var root = CreateTempDir("dotnet");
        var runtime = Path.Combine(root, "shared", "Microsoft.NETCore.App", "10.0.0");
        Directory.CreateDirectory(runtime);
        Directory.CreateDirectory(Path.Combine(root, "sdk"));
        var sdkOnly = CreateTempDir("sdkonly");
        var sdkOnlyChild = Path.Combine(sdkOnly, "shared", "Microsoft.NETCore.App", "10.0.0");
        Directory.CreateDirectory(sdkOnlyChild);

        try
        {
            var found = ToolchainRoots.FindInstallRoot(runtime);
            Assert.False(string.IsNullOrEmpty(found));
            Assert.Equal(Path.GetFullPath(root), Path.GetFullPath(found));

            var missed = ToolchainRoots.FindInstallRoot(sdkOnlyChild);
            if (missed is not null)
            {
                Assert.NotEqual(Path.GetFullPath(sdkOnly), Path.GetFullPath(missed));
            }
        }
        finally
        {
            TryDelete(root);
            TryDelete(sdkOnly);
        }
    }

    [Fact]
    public void discover_is_not_empty_on_a_machine_with_dotnet()
    {
        Assert.NotEmpty(ToolchainRoots.Discover().Roots);
    }

    private static string CreateTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), "dotnet-mcp-tc-" + label + "-" + Guid.NewGuid().ToString("N"));
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
