using System.Xml.Linq;

namespace DotNetMcp.Tests;

/// <summary>
/// Direct package versions live in the root Directory.Packages.props. Fixtures and spikes pin their
/// own versions on purpose, so each opts out with its own Directory.Packages.props.
/// </summary>
public class CentralPackageSeamTests
{
    // Packages of one family ship together and must move together.
    private static readonly string[] Families =
    [
        "Microsoft.CodeAnalysis.",
        "ModelContextProtocol",
        "Microsoft.Extensions.",
    ];

    [Fact]
    public void root_props_enables_central_management_and_declares_each_package_once()
    {
        var props = LoadRootProps();
        Assert.Equal("true", props.Descendants("ManagePackageVersionsCentrally").Single().Value);

        var versions = PackageVersions(props);
        Assert.NotEmpty(versions);
        Assert.Equal(
            versions.Length,
            versions.Select(static v => v.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(versions, static v => Assert.False(string.IsNullOrWhiteSpace(v.Version), v.Name));
    }

    [Fact]
    public void packages_of_one_family_share_a_version()
    {
        var versions = PackageVersions(LoadRootProps());
        foreach (var family in Families)
        {
            var members = versions
                .Where(v => v.Name.StartsWith(family, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.NotEmpty(members);

            var distinct = members.Select(static m => m.Version).Distinct(StringComparer.Ordinal).ToArray();
            Assert.True(
                distinct.Length == 1,
                $"{family}* packages must move together, but use: "
                + string.Join(", ", members.Select(static m => $"{m.Name} {m.Version}")));
        }
    }

    [Fact]
    public void project_files_reference_packages_without_a_version()
    {
        var root = FindRepoRoot();
        var declared = PackageVersions(LoadRootProps())
            .Select(static v => v.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var projects = ManagedProjectFiles(root).ToArray();
        Assert.True(projects.Length >= 6, "Expected the src, test and bench projects to be found.");

        foreach (var project in projects)
        {
            foreach (var reference in XDocument.Load(project).Descendants("PackageReference"))
            {
                var name = (string?)reference.Attribute("Include") ?? "(no Include)";
                var where = $"{Path.GetRelativePath(root, project)}: {name}";

                Assert.True(
                    reference.Attribute("Version") is null && reference.Element("Version") is null,
                    $"{where} pins a version. Versions live in Directory.Packages.props.");
                Assert.True(
                    reference.Attribute("VersionOverride") is null,
                    $"{where} overrides the central version.");
                Assert.True(
                    declared.Contains(name),
                    $"{where} has no PackageVersion in Directory.Packages.props.");
            }
        }
    }

    [Theory]
    [InlineData("tests/fixtures")]
    [InlineData("spikes")]
    public void fixtures_and_spikes_opt_out_of_central_package_management(string relativeDirectory)
    {
        var path = Path.Combine(
            FindRepoRoot(),
            relativeDirectory.Replace('/', Path.DirectorySeparatorChar),
            "Directory.Packages.props");
        Assert.True(
            File.Exists(path),
            $"{relativeDirectory}/Directory.Packages.props is missing, so central versions would apply to it.");

        Assert.Equal("false", XDocument.Load(path).Descendants("ManagePackageVersionsCentrally").Single().Value);
    }

    [Fact]
    public void test_output_carries_the_opt_out_beside_the_copied_fixtures()
    {
        // MSBuildWorkspace loads the fixture copies under the test output directory, which is still
        // inside the repository tree. Without this copy the root central versions would apply to them.
        var copied = Path.Combine(MsBuildWorkspaceIntegrationTests.FixturesRoot, "Directory.Packages.props");
        Assert.True(
            File.Exists(copied),
            "tests/fixtures/Directory.Packages.props was not copied to the test output directory.");
    }

    private static XDocument LoadRootProps() =>
        XDocument.Load(Path.Combine(FindRepoRoot(), "Directory.Packages.props"));

    private static (string Name, string? Version)[] PackageVersions(XDocument props) =>
        props.Descendants("PackageVersion")
            .Select(static e => (Name: (string?)e.Attribute("Include") ?? string.Empty, Version: (string?)e.Attribute("Version")))
            .ToArray();

    private static IEnumerable<string> ManagedProjectFiles(string root)
    {
        var searchRoots = new[]
        {
            Path.Combine(root, "src"),
            Path.Combine(root, "tests", "DotNetMcp.Tests"),
            Path.Combine(root, "benches"),
        };

        return searchRoots
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories))
            .Where(path => !Path.GetRelativePath(root, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(static segment => segment is "bin" or "obj"));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DotNetMcp.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test host.");
    }
}
