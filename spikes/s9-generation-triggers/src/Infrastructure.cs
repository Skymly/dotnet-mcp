using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;

namespace S9.Verification;

internal static class MsBuildBootstrap
{
    private static readonly object Gate = new();
    private static bool _registered;

    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }

            _registered = true;
        }
    }
}

internal static class Paths
{
    public static string SpikeRoot { get; } = FindSpikeRoot();

    public static string ToolkitAppProject { get; } =
        Path.Combine(SpikeRoot, "fixtures", "ToolkitApp", "ToolkitApp.csproj");

    private static string FindSpikeRoot([CallerFilePath] string? thisFile = null)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (dir is not null)
        {
            if (dir.Name == "s9-generation-triggers")
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate spikes/s9-generation-triggers.");
    }
}

/// <summary>Opens the ToolkitApp fixture once per test run.</summary>
internal static class FixtureWorkspace
{
    private static readonly Lazy<Task<(Project Project, Compilation Compilation, ImmutableArray<SourceGeneratedDocument> Docs, ImmutableArray<GeneratorInfo> Generators, ImmutableArray<SyntaxTree> GeneratedTrees)>> Lazy
        = new(OpenCoreAsync);

    public static Task<(Project Project, Compilation Compilation, ImmutableArray<SourceGeneratedDocument> Docs, ImmutableArray<GeneratorInfo> Generators, ImmutableArray<SyntaxTree> GeneratedTrees)> OpenAsync()
        => Lazy.Value;

    private static async Task<(Project, Compilation, ImmutableArray<SourceGeneratedDocument>, ImmutableArray<GeneratorInfo>, ImmutableArray<SyntaxTree>)> OpenCoreAsync()
    {
        MsBuildBootstrap.EnsureRegistered();

        var workspace = MSBuildWorkspace.Create();
        var project = await workspace.OpenProjectAsync(Paths.ToolkitAppProject);
        var compilation = await project.GetCompilationAsync()
            ?? throw new InvalidOperationException("Fixture compilation was null.");
        var docs = (await project.GetSourceGeneratedDocumentsAsync())
            .OfType<SourceGeneratedDocument>()
            .ToImmutableArray();

        var generatedTrees = new List<SyntaxTree>();
        foreach (var doc in docs)
        {
            var tree = await doc.GetSyntaxTreeAsync();
            if (tree is not null)
            {
                // Prefer the reference-equal instance on the compilation (S1/Q2 finding).
                var match = compilation.SyntaxTrees.FirstOrDefault(t => ReferenceEquals(t, tree))
                    ?? compilation.SyntaxTrees.FirstOrDefault(t =>
                        string.Equals(t.FilePath, tree.FilePath, StringComparison.OrdinalIgnoreCase));
                generatedTrees.Add(match ?? tree);
            }
        }

        var generators = project.AnalyzerReferences
            .SelectMany(r => r.GetGenerators(LanguageNames.CSharp))
            .Select(g =>
            {
                var type = g.GetGeneratorType();
                return new GeneratorInfo(
                    g,
                    type.Assembly.GetName().Name ?? "",
                    type.FullName ?? type.Name,
                    type.Assembly.GetName().Version?.ToString() ?? "",
                    Attr(type, "System.Reflection.AssemblyFileVersionAttribute"),
                    Attr(type, "System.Reflection.AssemblyInformationalVersionAttribute"));
            })
            .OrderBy(i => i.AssemblyName, StringComparer.Ordinal)
            .ThenBy(i => i.TypeFullName, StringComparer.Ordinal)
            .ToImmutableArray();

        return (project, compilation, docs, generators, generatedTrees.ToImmutableArray());
    }

    private static string Attr(Type type, string attributeName)
    {
        var attr = type.Assembly.GetCustomAttributes()
            .FirstOrDefault(a => a.GetType().FullName == attributeName);
        if (attr is null)
        {
            return "";
        }

        var prop = attr.GetType().GetProperty("Version")
            ?? attr.GetType().GetProperty("InformationalVersion");
        return prop?.GetValue(attr)?.ToString() ?? "";
    }

    public static bool IsGeneratedTree(SyntaxTree tree, ImmutableArray<SyntaxTree> generatedTrees)
        => generatedTrees.Any(t => ReferenceEquals(t, tree)
            || string.Equals(t.FilePath, tree.FilePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses the MSBuildWorkspace generated-document FilePath heuristic.
    /// Observed layouts: net8-era `.../obj/{cfg}/{tfm}/generated/{asm}/{type}/{hint}` (S1);
    /// net10-era `.../obj/{cfg}/{tfm}/{asm}/{type}/{hint}` (this spike). Both end with
    /// `{asm}/{type}/{hint}`.</summary>
    public static (string? Assembly, string? Type, string Hint) ParseGeneratedFilePath(string filePath)
    {
        var normalized = filePath.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var generatedIndex = Array.FindLastIndex(
            segments, s => string.Equals(s, "generated", StringComparison.OrdinalIgnoreCase));
        if (generatedIndex >= 0 && segments.Length - generatedIndex - 1 >= 3)
        {
            return (segments[generatedIndex + 1], segments[generatedIndex + 2], segments[generatedIndex + 3]);
        }

        // net10 layout: last three segments are {asm}/{type}/{hint}.
        if (segments.Length >= 3)
        {
            return (segments[^3], segments[^2], segments[^1]);
        }

        return (null, null, segments.Length > 0 ? segments[^1] : filePath);
    }
}

internal sealed record GeneratorInfo(
    ISourceGenerator Generator,
    string AssemblyName,
    string TypeFullName,
    string AssemblyVersion,
    string FileVersion,
    string InformationalVersion)
{
    public string IdentityString => $"{AssemblyName}::{TypeFullName}@{AssemblyVersion}";
}

/// <summary>Helpers for in-memory compilations used by the partial-pairing tests.</summary>
internal static class InMemory
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> LazyRefs = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToImmutableArray());

    public static ImmutableArray<MetadataReference> References => LazyRefs.Value;

    public static CSharpCompilation CompileCSharp(params (string Path, string Source)[] files) =>
        CSharpCompilation.Create(
            "InMemory",
            files.Select(f => CSharpSyntaxTree.ParseText(
                SourceText.From(f.Source),
                new CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Preview),
                path: f.Path)),
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    public static VisualBasicCompilation CompileVisualBasic(params (string Path, string Source)[] files) =>
        VisualBasicCompilation.Create(
            "InMemoryVb",
            files.Select(f => VisualBasicSyntaxTree.ParseText(
                SourceText.From(f.Source), path: f.Path)),
            References,
            new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
}

/// <summary>Minimal generator emitting a partial-method *implementation* part.
/// Instantiated directly into the driver — no [Generator] attribute needed.</summary>
public sealed class EmitPartialImplementationGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context)
    {
    }

    public void Execute(GeneratorExecutionContext context) =>
        context.AddSource(
            "Gen.impl.g.cs",
            SourceText.From("partial class Gen { partial void Hook() { } }", System.Text.Encoding.UTF8));
}

/// <summary>Minimal generator emitting a partial-method *definition* part.</summary>
public sealed class EmitPartialDefinitionGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context)
    {
    }

    public void Execute(GeneratorExecutionContext context) =>
        context.AddSource(
            "Gen.def.g.cs",
            SourceText.From("partial class Gen { partial void Hook(); }", System.Text.Encoding.UTF8));
}
