using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Core;

/// <summary>
/// Strip workspace-generated trees and run a public <see cref="CSharpGeneratorDriver"/> (ADR-0001 §6 / Spike S1).
/// </summary>
public static class GeneratorDriverRunner
{
    public static async Task<Compilation> StripGeneratedTreesFromProjectAsync(
        Project project,
        Compilation compilation,
        CancellationToken cancellationToken = default)
    {
        var generatedDocs = (await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false))
            .OfType<SourceGeneratedDocument>()
            .ToImmutableArray();

        return await StripGeneratedTreesAsync(compilation, generatedDocs, cancellationToken)
            .ConfigureAwait(false);
    }

    public static DriverRunSnapshot RunDriver(
        Project project,
        Compilation baseCompilation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var generators = project.AnalyzerReferences
            .SelectMany(r => r.GetGenerators(project.Language))
            .ToImmutableArray();

        var additionalTexts = project.AdditionalDocuments
            .Select(d => (AdditionalText)new WorkspaceAdditionalText(d))
            .ToImmutableArray();

        GeneratorDriver driver;
        if (project.Language == LanguageNames.VisualBasic)
        {
            driver = VisualBasicGeneratorDriver.Create(
                generators,
                additionalTexts,
                project.ParseOptions as VisualBasicParseOptions ?? VisualBasicParseOptions.Default,
                project.AnalyzerOptions.AnalyzerConfigOptionsProvider);
        }
        else
        {
            driver = CSharpGeneratorDriver.Create(
                generators,
                additionalTexts: additionalTexts,
                parseOptions: project.ParseOptions as CSharpParseOptions ?? CSharpParseOptions.Default,
                optionsProvider: project.AnalyzerOptions.AnalyzerConfigOptionsProvider);
        }

        driver = driver.RunGeneratorsAndUpdateCompilation(
            baseCompilation,
            out _,
            out _,
            cancellationToken);

        var runResult = driver.GetRunResult();
        var byGenerator = new List<GeneratorRunSources>();
        var flat = new List<GeneratedSourceMatch>();

        foreach (var result in runResult.Results)
        {
            var type = result.Generator.GetGeneratorType();
            var identity = new GeneratorIdentity(
                type.Assembly.GetName().Name ?? string.Empty,
                type.FullName ?? type.Name,
                type.Assembly.GetName().Version?.ToString() ?? "0.0.0.0");

            var sources = new List<GeneratedSourceItem>(result.GeneratedSources.Length);
            foreach (var source in result.GeneratedSources)
            {
                var content = source.SourceText.ToString();
                var item = new GeneratedSourceItem(source.HintName, content);
                sources.Add(item);
                flat.Add(new GeneratedSourceMatch(identity, source.HintName, content, source.SyntaxTree));
            }

            var diagnostics = result.Diagnostics
                .Select(static d => new GeneratorDiagnosticItem(
                    d.Id,
                    d.Severity.ToString(),
                    d.GetMessage()))
                .OrderBy(static d => d.Id, StringComparer.Ordinal)
                .ThenBy(static d => d.Severity, StringComparer.Ordinal)
                .ThenBy(static d => d.Message, StringComparer.Ordinal)
                .ToArray();

            byGenerator.Add(new GeneratorRunSources(identity, sources, diagnostics));
        }

        byGenerator.Sort(static (a, b) =>
        {
            var assembly = string.CompareOrdinal(a.Identity.AssemblyName, b.Identity.AssemblyName);
            return assembly != 0
                ? assembly
                : string.CompareOrdinal(a.Identity.TypeFullName, b.Identity.TypeFullName);
        });

        return new DriverRunSnapshot(byGenerator, flat);
    }

    public static GeneratorIdentity? MatchTree(DriverRunSnapshot snapshot, SyntaxTree tree)
    {
        foreach (var source in snapshot.FlatSources)
        {
            if (ReferenceEquals(tree, source.SyntaxTree))
            {
                return source.Identity;
            }
        }

        // Content is the public contract (ADR-0001 §6). Do not trust FilePath/HintName alone —
        // colliding HintNames share the same short path segment across generators.
        var targetText = tree.GetText().ToString();
        foreach (var source in snapshot.FlatSources)
        {
            if (string.Equals(source.Content, targetText, StringComparison.Ordinal))
            {
                return source.Identity;
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

        return string.Equals(a.GetText().ToString(), b.GetText().ToString(), StringComparison.Ordinal);
    }

    private static async Task<Compilation> StripGeneratedTreesAsync(
        Compilation compilation,
        ImmutableArray<SourceGeneratedDocument> generatedDocs,
        CancellationToken cancellationToken)
    {
        if (generatedDocs.Length == 0)
        {
            return compilation;
        }

        var trees = new List<SyntaxTree>(generatedDocs.Length);
        foreach (var doc in generatedDocs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var docTree = await doc.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            if (docTree is null)
            {
                continue;
            }

            var matching = compilation.SyntaxTrees.FirstOrDefault(t => ReferenceEquals(t, docTree))
                ?? compilation.SyntaxTrees.FirstOrDefault(t =>
                    string.Equals(t.FilePath, docTree.FilePath, StringComparison.OrdinalIgnoreCase));

            trees.Add(matching ?? docTree);
        }

        return compilation.RemoveSyntaxTrees(trees);
    }

    private sealed class WorkspaceAdditionalText : AdditionalText
    {
        private readonly TextDocument _document;

        public WorkspaceAdditionalText(TextDocument document) => _document = document;

        public override string Path => _document.FilePath ?? _document.Name;

        public override SourceText? GetText(CancellationToken cancellationToken = default)
            => _document.GetTextAsync(cancellationToken).GetAwaiter().GetResult();
    }
}
