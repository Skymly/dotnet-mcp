using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace S1.Verification;

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

internal static class FixturePaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string SpikeRoot { get; } = Path.Combine(RepoRoot, "spikes", "s1-generator-attribution");

    public static string SampleAppProject { get; } = Path.Combine(SpikeRoot, "fixtures", "SampleApp", "SampleApp.csproj");

    public static string CollisionHostProject { get; } = Path.Combine(SpikeRoot, "fixtures", "CollisionHost", "CollisionHost.csproj");

    private static string FindRepoRoot([CallerFilePath] string? thisFile = null)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "docs", "adr")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root from test assembly path.");
    }
}

internal sealed class WorkspaceSession : IAsyncDisposable
{
    private readonly MSBuildWorkspace _workspace;

    private WorkspaceSession(MSBuildWorkspace workspace, Project project)
    {
        _workspace = workspace;
        Project = project;
    }

    public Project Project { get; private set; }

    public static async Task<WorkspaceSession> OpenAsync(string projectPath, CancellationToken ct = default)
    {
        MsBuildBootstrap.EnsureRegistered();

        var workspace = MSBuildWorkspace.Create();
#pragma warning disable CS0618 // WorkspaceFailed obsolete in 5.x; fine for spike diagnostics
        workspace.WorkspaceFailed += (_, e) =>
            Debug.WriteLine($"[MSBuildWorkspace] {e.Diagnostic.Kind}: {e.Diagnostic.Message}");
#pragma warning restore CS0618

        var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: ct);
        return new WorkspaceSession(workspace, project);
    }

    public ValueTask DisposeAsync()
    {
        _workspace.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal static class AttributionHelpers
{
    public static async Task<(Compilation Compilation, ImmutableArray<SourceGeneratedDocument> Docs)>
        GetCompilationAndGeneratedDocsAsync(Project project, CancellationToken ct = default)
    {
        var docs = await project.GetSourceGeneratedDocumentsAsync(ct);
        var compilation = await project.GetCompilationAsync(ct)
            ?? throw new InvalidOperationException("Compilation was null.");
        return (compilation, docs.OfType<SourceGeneratedDocument>().ToImmutableArray());
    }

    public static Compilation StripGeneratedTrees(
        Compilation compilation,
        ImmutableArray<SourceGeneratedDocument> generatedDocs)
    {
        var trees = new List<SyntaxTree>();
        foreach (var doc in generatedDocs)
        {
            var docTree = doc.GetSyntaxTreeAsync().GetAwaiter().GetResult();
            if (docTree is null)
            {
                continue;
            }

            // Prefer the compilation instance when reference-equal (Q2 finding).
            var matching = compilation.SyntaxTrees.FirstOrDefault(t => ReferenceEquals(t, docTree))
                ?? compilation.SyntaxTrees.FirstOrDefault(t =>
                    string.Equals(t.FilePath, docTree.FilePath, StringComparison.OrdinalIgnoreCase));

            trees.Add(matching ?? docTree);
        }

        return compilation.RemoveSyntaxTrees(trees);
    }

    public static async Task<(Compilation BaseCompilation, GeneratorDriverRunResult RunResult, ImmutableArray<(ISourceGenerator Generator, string TypeName, string AssemblyName)> Generators)>
        RunDriverOnBaseAsync(Project project, CancellationToken ct = default)
    {
        var (compilation, docs) = await GetCompilationAndGeneratedDocsAsync(project, ct);
        var baseCompilation = StripGeneratedTrees(compilation, docs);

        var generators = project.AnalyzerReferences
            .SelectMany(r => r.GetGenerators(LanguageNames.CSharp)
                .Select(g =>
                {
                    var type = g.GetGeneratorType();
                    return (Generator: g, TypeName: type.FullName ?? type.Name, AssemblyName: type.Assembly.GetName().Name ?? "");
                }))
            .ToImmutableArray();

        var parseOptions = project.ParseOptions as CSharpParseOptions ?? CSharpParseOptions.Default;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators.Select(g => g.Generator),
            additionalTexts: project.AdditionalDocuments
                .Select(d => (AdditionalText)new WorkspaceAdditionalText(d))
                .ToImmutableArray(),
            parseOptions: parseOptions,
            optionsProvider: project.AnalyzerOptions.AnalyzerConfigOptionsProvider);

        driver = driver.RunGeneratorsAndUpdateCompilation(baseCompilation, out _, out _, ct);
        var runResult = driver.GetRunResult();
        return (baseCompilation, runResult, generators);
    }

    public static GeneratorIdentity? TryReflectIdentity(SourceGeneratedDocument document)
    {
        var identityProp = typeof(SourceGeneratedDocument).GetProperty(
            "Identity",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (identityProp is null)
        {
            return null;
        }

        var identity = identityProp.GetValue(document);
        if (identity is null)
        {
            return null;
        }

        var identityType = identity.GetType();
        var generatorProp = identityType.GetProperty("Generator", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var hintProp = identityType.GetProperty("HintName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var filePathProp = identityType.GetProperty("FilePath", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var generator = generatorProp?.GetValue(identity);
        if (generator is null)
        {
            return null;
        }

        var gType = generator.GetType();
        string? ReadString(string name) => gType.GetProperty(name)?.GetValue(generator)?.ToString();
        var version = gType.GetProperty("AssemblyVersion")?.GetValue(generator);

        return new GeneratorIdentity(
            AssemblyName: ReadString("AssemblyName") ?? "",
            AssemblyPath: ReadString("AssemblyPath"),
            AssemblyVersion: version?.ToString() ?? "",
            TypeName: ReadString("TypeName") ?? "",
            HintName: hintProp?.GetValue(identity)?.ToString() ?? document.HintName,
            IdentityFilePath: filePathProp?.GetValue(identity)?.ToString());
    }

    public static async Task<string?> ResolveGeneratorViaDriverAsync(
        Project project,
        ISymbol symbol,
        CancellationToken ct = default)
    {
        var declaring = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (declaring is null)
        {
            return null;
        }

        var tree = declaring.SyntaxTree;
        var (_, runResult, _) = await RunDriverOnBaseAsync(project, ct);

        foreach (var result in runResult.Results)
        {
            foreach (var source in result.GeneratedSources)
            {
                if (TreesMatch(tree, source.SyntaxTree))
                {
                    var type = result.Generator.GetGeneratorType();
                    return $"{type.Assembly.GetName().Name}::{type.FullName}";
                }
            }
        }

        // Content-based fallback (tree instance may differ).
        var targetText = tree.GetText(ct).ToString();
        foreach (var result in runResult.Results)
        {
            foreach (var source in result.GeneratedSources)
            {
                if (string.Equals(source.SourceText.ToString(), targetText, StringComparison.Ordinal))
                {
                    var type = result.Generator.GetGeneratorType();
                    return $"{type.Assembly.GetName().Name}::{type.FullName}";
                }
            }
        }

        return null;
    }

    public static bool TreesMatch(SyntaxTree a, SyntaxTree b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(a.FilePath) &&
            string.Equals(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(a.GetText().ToString(), b.GetText().ToString(), StringComparison.Ordinal);
    }

    public static string DumpGeneratedDocs(ImmutableArray<SourceGeneratedDocument> docs)
    {
        var sb = new StringBuilder();
        foreach (var d in docs.OrderBy(d => d.HintName, StringComparer.Ordinal))
        {
            sb.AppendLine($"HintName={d.HintName}");
            sb.AppendLine($"  Name={d.Name}");
            sb.AppendLine($"  FilePath={d.FilePath}");
            sb.AppendLine($"  Id={d.Id}");
            sb.AppendLine($"  Type={d.GetType().Name}");
        }

        return sb.ToString();
    }
}

internal readonly record struct GeneratorIdentity(
    string AssemblyName,
    string? AssemblyPath,
    string AssemblyVersion,
    string TypeName,
    string HintName,
    string? IdentityFilePath);

internal sealed class WorkspaceAdditionalText : AdditionalText
{
    private readonly TextDocument _document;

    public WorkspaceAdditionalText(TextDocument document) => _document = document;

    public override string Path => _document.FilePath ?? _document.Name;

    public override SourceText? GetText(CancellationToken cancellationToken = default)
        => _document.GetTextAsync(cancellationToken).GetAwaiter().GetResult();
}
