using System.Reflection;
using DotNetMcp.Server;
using FSharp.Compiler.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Tests;

/// <summary>
/// The server loads project analyzers and source generators into its own Roslyn, and checks F# projects with
/// its own FSharp.Compiler.Service. If the newest installed SDK ships a newer compiler than the one bundled
/// here, generators built for that SDK are refused (CS9057 / ReferencesNewerCompiler, so the generator list
/// comes back empty) and F# language features of that SDK may be reported as errors. CI floats on the
/// <c>10.0.x</c> SDK feature band, so this gate turns red as soon as a newer band is published.
/// </summary>
public class CompilerVersionGateTests
{
    [Fact]
    public void bundled_roslyn_is_not_older_than_the_roslyn_in_the_newest_sdk()
    {
        var sdk = FindNewestSdk();
        var sdkRoslyn = Path.Combine(sdk, "Roslyn", "bincore", "Microsoft.CodeAnalysis.dll");
        var sdkVersion = ReadAssemblyVersion(sdkRoslyn);
        var bundledVersion = typeof(Compilation).Assembly.GetName().Version!;

        Assert.True(
            AtLeast(bundledVersion, sdkVersion),
            $"Bundled Roslyn is {bundledVersion}, but SDK {Path.GetFileName(sdk)} ships Roslyn {sdkVersion}. "
            + "Analyzers and generators built for that SDK would be refused as referencing a newer compiler. "
            + "Raise the Microsoft.CodeAnalysis.* versions in Directory.Packages.props.");
    }

    [Fact]
    public void bundled_fsharp_compiler_service_is_not_older_than_the_one_in_the_newest_sdk()
    {
        var sdk = FindNewestSdk();
        var sdkFcs = Path.Combine(sdk, "FSharp", "FSharp.Compiler.Service.dll");
        var sdkVersion = ReadAssemblyVersion(sdkFcs);
        var bundledVersion = typeof(FSharpChecker).Assembly.GetName().Version!;

        Assert.True(
            AtLeast(bundledVersion, sdkVersion),
            $"Bundled FSharp.Compiler.Service is {bundledVersion}, but SDK {Path.GetFileName(sdk)} ships {sdkVersion}. "
            + "F# projects using that SDK's language features could be reported as errors. "
            + "Raise FSharp.Compiler.Service in Directory.Packages.props.");
    }

    [Theory]
    [InlineData("5.9.0.0", "5.6.0.0", true)]
    [InlineData("5.6.0.0", "5.6.0.0", true)]
    [InlineData("5.6.0.0", "5.9.0.0", false)]
    [InlineData("6.0.0.0", "5.9.0.0", true)]
    [InlineData("4.14.0.0", "5.0.0.0", false)]
    // Patch and revision do not matter: 43.12.402 is not "older" than 43.12.500, only 43.11 is older than 43.12.
    [InlineData("43.12.402.0", "43.12.500.0", true)]
    [InlineData("43.10.109.0", "43.11.303.0", false)]
    public void at_least_compares_major_and_minor_only(string bundled, string sdk, bool expected)
    {
        Assert.Equal(expected, AtLeast(Version.Parse(bundled), Version.Parse(sdk)));
    }

    private static bool AtLeast(Version bundled, Version sdk) =>
        bundled.Major > sdk.Major || (bundled.Major == sdk.Major && bundled.Minor >= sdk.Minor);

    private static string FindNewestSdk()
    {
        var sdk = MsBuildBootstrap.TryFindNewestSdkDirectory();
        Assert.True(
            sdk is not null,
            "No .NET SDK was found to compare compiler versions against. Set DOTNET_ROOT to the dotnet install "
            + "that holds the SDK, the same one the server registers MSBuild from.");
        return sdk!;
    }

    private static Version ReadAssemblyVersion(string path)
    {
        Assert.True(File.Exists(path), $"Expected the SDK to ship {path}.");
        return AssemblyName.GetAssemblyName(path).Version!;
    }
}
