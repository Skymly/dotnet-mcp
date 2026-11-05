using DotNetMcp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DotNetMcp.Tests;

/// <summary>
/// Issue #577 unit coverage for the searched-degraded package-cache signal:
/// the root computation reads env-var values through an internal seam (no
/// process environment or file system needed) and the snapshot probe counts
/// only references parked under one of those roots.
/// </summary>
public class FinderScopeCoverageTests
{
    [Fact]
    public void ComputeNuGetPackageRoots_prefers_NUGET_PACKAGES_over_profile_default()
    {
        var env = Path.Combine(Path.GetTempPath(), "custom-feed");
        var roots = FinderScopeCoverage.ComputeNuGetPackageRoots(
            env, nugetFallbackEnv: null, userProfilePath: Path.Combine("home", "user"));

        var root = Assert.Single(roots);
        Assert.Equal(Path.GetFullPath(env), root);
    }

    [Fact]
    public void ComputeNuGetPackageRoots_defaults_to_profile_nuget_cache()
    {
        var profile = Path.Combine(Path.GetTempPath(), "profile");
        var roots = FinderScopeCoverage.ComputeNuGetPackageRoots(
            nugetPackagesEnv: null, nugetFallbackEnv: null, userProfilePath: profile);

        var root = Assert.Single(roots);
        Assert.Equal(Path.Combine(profile, ".nuget", "packages"), root);
    }

    [Fact]
    public void ComputeNuGetPackageRoots_appends_fallback_list_entries()
    {
        var fallbackA = Path.Combine(Path.GetTempPath(), "fb-a");
        var fallbackB = Path.Combine(Path.GetTempPath(), "fb-b");
        var roots = FinderScopeCoverage.ComputeNuGetPackageRoots(
            nugetPackagesEnv: Path.Combine(Path.GetTempPath(), "feed"),
            nugetFallbackEnv: $"{fallbackA},{fallbackB}",
            userProfilePath: null);

        Assert.Equal(
            new[]
            {
                Path.GetFullPath(Path.Combine(Path.GetTempPath(), "feed")),
                Path.GetFullPath(fallbackA),
                Path.GetFullPath(fallbackB),
            },
            roots);
    }

    [Fact]
    public void ComputeNuGetPackageRoots_semicolon_fallback_entries_also_count()
    {
        var fallback = Path.Combine(Path.GetTempPath(), "fb-semi");
        var roots = FinderScopeCoverage.ComputeNuGetPackageRoots(
            nugetPackagesEnv: null,
            nugetFallbackEnv: $";{fallback};",
            userProfilePath: null);

        var root = Assert.Single(roots);
        Assert.Equal(Path.GetFullPath(fallback), root);
    }

    [Fact]
    public void ComputeNuGetPackageRoots_empty_when_nothing_resolves()
    {
        var roots = FinderScopeCoverage.ComputeNuGetPackageRoots(null, null, null);
        Assert.Empty(roots);
    }

    [Fact]
    public void ComputeNuGetPackageRoots_deduplicates_and_trims_separators()
    {
        var feed = Path.Combine(Path.GetTempPath(), "feed");
        var roots = FinderScopeCoverage.ComputeNuGetPackageRoots(
            nugetPackagesEnv: feed + Path.DirectorySeparatorChar,
            nugetFallbackEnv: feed,
            userProfilePath: null);

        var root = Assert.Single(roots);
        Assert.Equal(Path.GetFullPath(feed), root);
    }

    [Fact]
    public void HoldsPackageCacheReference_true_for_reference_under_root()
    {
        var assemblyPath = typeof(object).Assembly.Location;
        var compilation = CSharpCompilation.Create(
            "Probe",
            references: [MetadataReference.CreateFromFile(assemblyPath)]);

        var root = Path.GetDirectoryName(assemblyPath)!;
        Assert.True(FinderScopeCoverage.HoldsPackageCacheReference(compilation, [root]));
    }

    [Fact]
    public void HoldsPackageCacheReference_false_for_framework_path_outside_root()
    {
        var assemblyPath = typeof(object).Assembly.Location;
        var compilation = CSharpCompilation.Create(
            "Probe",
            references: [MetadataReference.CreateFromFile(assemblyPath)]);

        var elsewhere = Path.Combine(Path.GetTempPath(), "not-the-cache");
        Assert.False(FinderScopeCoverage.HoldsPackageCacheReference(compilation, [elsewhere]));
    }

    [Fact]
    public void HoldsPackageCacheReference_false_for_sibling_sharing_root_prefix()
    {
        // cache2 is not under cache even though the string shares the
        // root's prefix — the boundary check requires a separator.
        var baseDir = Path.Combine(Path.GetTempPath(), $"dnmcp-sibling-{Guid.NewGuid():N}");
        var rootDir = Path.Combine(baseDir, "cache");
        var siblingDir = Path.Combine(baseDir, "cache2");
        Directory.CreateDirectory(siblingDir);
        var dll = Path.Combine(siblingDir, "lib.dll");
        File.Copy(typeof(object).Assembly.Location, dll, overwrite: true);

        var compilation = CSharpCompilation.Create(
            "Probe",
            references: [MetadataReference.CreateFromFile(dll)]);

        Assert.False(FinderScopeCoverage.HoldsPackageCacheReference(compilation, [rootDir]));
    }

    [Fact]
    public void HoldsPackageCacheReference_false_for_image_reference_without_filepath()
    {
        // A reference created from an in-memory image carries no FilePath —
        // it cannot sit under a cache root and must not count.
        var image = File.ReadAllBytes(typeof(object).Assembly.Location);
        var compilation = CSharpCompilation.Create(
            "Probe",
            references: [MetadataReference.CreateFromImage(image)]);

        var root = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        Assert.False(FinderScopeCoverage.HoldsPackageCacheReference(compilation, [root]));
    }

    [Fact]
    public void HoldsPackageCacheReference_false_for_null_compilation()
    {
        Assert.False(FinderScopeCoverage.HoldsPackageCacheReference(
            compilation: null, packageRoots: [Path.GetTempPath()]));
    }
}
