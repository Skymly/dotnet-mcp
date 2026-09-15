using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class ToolchainRootsTests
{
    [Fact]
    public void discover_includes_nuget_packages_when_the_directory_exists()
    {
        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(packages))
        {
            packages = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget",
                "packages");
        }

        if (!Directory.Exists(packages))
        {
            return;
        }

        var roots = ToolchainRoots.Discover();
        Assert.True(roots.Contains(packages));
        Assert.True(roots.Contains(Path.Combine(packages, "example.pkg", "analyzers", "dotnet", "cs", "Example.dll")));
    }

    [Fact]
    public void discover_is_not_empty_on_a_machine_with_dotnet()
    {
        Assert.NotEmpty(ToolchainRoots.Discover().Roots);
    }
}
