using System.Xml.Linq;
using DotNetMcp.Server;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Tests;

/// <summary>
/// The repository root Directory.Build.props turns warnings into errors for product code. Fixtures
/// and spikes are test material with deliberate warnings, so each has an empty Directory.Build.props
/// that stops the inheritance.
/// </summary>
public class BuildPropsSeamTests
{
    private static readonly string[] NuGetAuditCodes = ["NU1901", "NU1902", "NU1903", "NU1904"];

    [Fact]
    public void root_build_props_treats_warnings_as_errors_except_nuget_audit()
    {
        var props = XDocument.Load(Path.Combine(FindRepoRoot(), "Directory.Build.props"));

        Assert.Equal("true", PropertyValue(props, "TreatWarningsAsErrors"));

        var notAsErrors = PropertyValue(props, "WarningsNotAsErrors");
        Assert.NotNull(notAsErrors);
        var codes = notAsErrors.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var code in NuGetAuditCodes)
        {
            Assert.Contains(code, codes);
        }
    }

    [Fact]
    public void root_build_props_sets_continuous_integration_build_only_on_github_actions()
    {
        var props = XDocument.Load(Path.Combine(FindRepoRoot(), "Directory.Build.props"));

        var group = Assert.Single(
            props.Descendants("PropertyGroup"),
            static g => g.Element("ContinuousIntegrationBuild") is not null);
        Assert.Equal("'$(GITHUB_ACTIONS)' == 'true'", (string?)group.Attribute("Condition"));
        Assert.Equal("true", group.Element("ContinuousIntegrationBuild")!.Value);
    }

    [Theory]
    [InlineData("tests/fixtures")]
    [InlineData("spikes")]
    public void fixtures_and_spikes_have_an_empty_build_props_that_stops_the_root(string relativeDirectory)
    {
        var path = Path.Combine(
            FindRepoRoot(),
            relativeDirectory.Replace('/', Path.DirectorySeparatorChar),
            "Directory.Build.props");
        Assert.True(
            File.Exists(path),
            $"{relativeDirectory}/Directory.Build.props is missing, so the root props would apply to it.");

        var root = XDocument.Load(path).Root;
        Assert.NotNull(root);
        Assert.Equal("Project", root.Name.LocalName);
        Assert.Empty(root.Elements());
    }

    [Fact]
    public void test_output_carries_the_truncation_file_beside_the_copied_fixtures()
    {
        // MSBuildWorkspace loads the fixture copies under the test output directory, which is still
        // inside the repository tree. Without this copy the root props would apply to them.
        var copied = Path.Combine(MsBuildWorkspaceIntegrationTests.FixturesRoot, "Directory.Build.props");
        Assert.True(
            File.Exists(copied),
            "tests/fixtures/Directory.Build.props was not copied to the test output directory.");
    }

    [Fact]
    public async Task msbuild_workspace_does_not_inherit_warnings_as_errors_into_the_copied_fixtures()
    {
        var slnx = MsBuildWorkspaceIntegrationTests.SampleSlnx;
        Assert.True(File.Exists(slnx), $"Missing fixture: {slnx}");

        var trustedRoots = TrustedRoots.Create([Path.GetDirectoryName(slnx)!]);
        await using var loaded = await new MsBuildSolutionLoader(trustedRoots).OpenAsync(slnx);

        var projects = loaded.Solution.Projects.ToArray();
        Assert.NotEmpty(projects);
        Assert.All(
            projects,
            project => Assert.NotEqual(ReportDiagnostic.Error, project.CompilationOptions!.GeneralDiagnosticOption));
    }

    private static string? PropertyValue(XDocument props, string name) =>
        props.Descendants(name).Select(static e => e.Value).FirstOrDefault();

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
