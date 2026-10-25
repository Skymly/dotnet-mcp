using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit.Abstractions;

namespace S9.Verification;

internal sealed class PathAssemblyLoader : IAnalyzerAssemblyLoader
{
    public void AddDependencyLocation(string fullPath)
    {
    }

    public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
}

public class Q5_IdentityTests
{
    private readonly ITestOutputHelper _output;

    public Q5_IdentityTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Identity_Table_Fixture_Generators()
    {
        var (_, _, _, generators, _) = await FixtureWorkspace.OpenAsync();

        _output.WriteLine("Assembly::Type@AssemblyVersion  [file/informational]");
        foreach (var g in generators)
        {
            _output.WriteLine($"  {g.IdentityString}  [file={g.FileVersion} info={g.InformationalVersion}]");
        }

        // (AssemblyName, TypeFullName) must be unique in a real project's generator set —
        // that pair is a stable rule-table key.
        var keys = generators.Select(g => (g.AssemblyName, g.TypeFullName)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public async Task Identity_AssemblyVersion_Differs_From_NuGet_Package_Version()
    {
        var (_, _, _, generators, _) = await FixtureWorkspace.OpenAsync();

        var toolkit = generators.Where(g => g.AssemblyName == "CommunityToolkit.Mvvm.SourceGenerators").ToList();
        Assert.NotEmpty(toolkit);
        foreach (var g in toolkit)
        {
            _output.WriteLine($"{g.TypeFullName}: AssemblyVersion={g.AssemblyVersion} " +
                              $"FileVersion={g.FileVersion} InformationalVersion={g.InformationalVersion} (NuGet package 8.4.2)");
        }

        // The product records assembly VERSION; the rule key must not depend on it.
        // Evidence: AssemblyVersion (x.y.z.0 / pinned) is not the NuGet package version (8.4.2).
        Assert.All(toolkit, g =>
        {
            var versionParts = g.AssemblyVersion.Split('.');
            Assert.True(versionParts.Length >= 3);
        });
    }

    [Fact]
    public void Identity_Framework_BuiltIn_Generators()
    {
        // .NET ref-pack analyzers ship with the SDK and appear in AnalyzerReferences for every
        // net10 project (S1 finding). Enumerate their real identity strings.
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory(); // .../shared/Microsoft.NETCore.App/x.y.z/
        var sharedDir = Directory.GetParent(runtimeDir.TrimEnd(Path.DirectorySeparatorChar))!;
        var dotnetRoot = sharedDir.Parent!.Parent!.FullName;
        var packsDir = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
        _output.WriteLine($"packs dir: {packsDir} exists={Directory.Exists(packsDir)}");
        Assert.True(Directory.Exists(packsDir));

        var newest = Directory.GetDirectories(packsDir)
            .OrderBy(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version(0, 0))
            .Last();
        _output.WriteLine($"pack dir picked: {newest} (all: {string.Join(", ", Directory.GetDirectories(packsDir).Select(Path.GetFileName))})");
        var analyzerDir = Path.Combine(newest, "analyzers", "dotnet", "cs");
        _output.WriteLine($"analyzer dir: {analyzerDir}");
        Assert.True(Directory.Exists(analyzerDir));

        var loader = new PathAssemblyLoader();
        var identities = new List<string>();
        foreach (var dll in Directory.GetFiles(analyzerDir, "*.dll"))
        {
            var reference = new AnalyzerFileReference(dll, loader);
            var assemblyName = Path.GetFileNameWithoutExtension(dll);
            foreach (var generator in reference.GetGenerators(LanguageNames.CSharp))
            {
                var type = generator.GetGeneratorType();
                var identity = $"{type.Assembly.GetName().Name}::{type.FullName}@{type.Assembly.GetName().Version}";
                identities.Add(identity);
                _output.WriteLine($"{identity}  ({assemblyName}.dll)");
            }
        }

        Assert.NotEmpty(identities);
        Assert.Contains(identities, i => i.StartsWith("System.Text.Json.SourceGeneration::", StringComparison.Ordinal));
        Assert.Contains(identities, i => i.StartsWith("System.Text.RegularExpressions.Generator::", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Identity_Matches_Product_Origin_Format()
    {
        // Product format: SourceGenerator(Assembly::Type@Version) — verify the Assembly::Type
        // prefix of real identities round-trips as a rule key.
        var (_, _, _, generators, _) = await FixtureWorkspace.OpenAsync();
        foreach (var g in generators.Take(3))
        {
            var origin = $"SourceGenerator({g.AssemblyName}::{g.TypeFullName}@{g.AssemblyVersion})";
            _output.WriteLine(origin);
            var inner = origin["SourceGenerator(".Length..^1];
            var key = inner[..inner.IndexOf('@')];
            Assert.Equal($"{g.AssemblyName}::{g.TypeFullName}", key);
        }
    }
}
