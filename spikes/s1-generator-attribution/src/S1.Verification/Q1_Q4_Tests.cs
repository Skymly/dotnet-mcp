using Microsoft.CodeAnalysis;
using Xunit;
using Xunit.Abstractions;

namespace S1.Verification;

public sealed class Q1_FilePathFormatTests
{
    private readonly ITestOutputHelper _output;

    public Q1_FilePathFormatTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Source_generated_document_FilePath_Name_Id_are_observable_and_stable()
    {
        await using var session = await WorkspaceSession.OpenAsync(FixturePaths.SampleAppProject);
        var (_, docs) = await AttributionHelpers.GetCompilationAndGeneratedDocsAsync(session.Project);

        _output.WriteLine(AttributionHelpers.DumpGeneratedDocs(docs));

        Assert.True(docs.Length >= 2, $"Expected multiple generators' outputs, got {docs.Length}. Dump:\n{AttributionHelpers.DumpGeneratedDocs(docs)}");

        Assert.Contains(docs, d => d.HintName.Contains("PersonViewModel", StringComparison.OrdinalIgnoreCase) ||
                                    (d.FilePath?.Contains("ObservablePropertyGenerator", StringComparison.OrdinalIgnoreCase) ?? false));
        Assert.Contains(docs, d => d.FilePath?.Contains("System.Text.Json.SourceGeneration", StringComparison.OrdinalIgnoreCase) ?? false);
        Assert.Contains(docs, d => d.FilePath?.Contains("CustomGenerator.MarkerGenerator", StringComparison.OrdinalIgnoreCase) ?? false);

        foreach (var doc in docs)
        {
            Assert.False(string.IsNullOrWhiteSpace(doc.HintName));
            Assert.False(string.IsNullOrWhiteSpace(doc.Name));
            Assert.IsType<SourceGeneratedDocument>(doc);

            _output.WriteLine($"FilePath contains 'CommunityToolkit': {doc.FilePath?.Contains("CommunityToolkit", StringComparison.OrdinalIgnoreCase)}");
            _output.WriteLine($"FilePath contains 'ObservableProperty': {doc.FilePath?.Contains("ObservableProperty", StringComparison.OrdinalIgnoreCase)}");
            _output.WriteLine($"FilePath separator style: slash={doc.FilePath?.Contains('/')}, backslash={doc.FilePath?.Contains('\\')}");
            if (doc.FilePath is not null)
            {
                var lower = doc.FilePath.ToLowerInvariant();
                var upper = doc.FilePath.ToUpperInvariant();
                // Windows FS is case-insensitive; Roslyn still preserves the casing it was given.
                _output.WriteLine($"FilePath casing preserved (not forced lower/upper): neqLower={!string.Equals(doc.FilePath, lower, StringComparison.Ordinal)}, neqUpper={!string.Equals(doc.FilePath, upper, StringComparison.Ordinal)}");
            }
        }
    }
}

public sealed class Q2_StripGeneratedTreesTests
{
    private readonly ITestOutputHelper _output;

    public Q2_StripGeneratedTreesTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Generated_document_SyntaxTree_reference_equality_and_RemoveSyntaxTrees_yields_clean_base()
    {
        await using var session = await WorkspaceSession.OpenAsync(FixturePaths.SampleAppProject);
        var (compilation, docs) = await AttributionHelpers.GetCompilationAndGeneratedDocsAsync(session.Project);

        Assert.NotEmpty(docs);

        var refEqualCount = 0;
        var pathEqualCount = 0;
        var missingCount = 0;

        foreach (var doc in docs)
        {
            var docTree = await doc.GetSyntaxTreeAsync();
            Assert.NotNull(docTree);

            var inCompilation = compilation.SyntaxTrees.FirstOrDefault(t => ReferenceEquals(t, docTree));
            if (inCompilation is not null)
            {
                refEqualCount++;
            }
            else if (compilation.SyntaxTrees.Any(t =>
                         string.Equals(t.FilePath, docTree!.FilePath, StringComparison.OrdinalIgnoreCase)))
            {
                pathEqualCount++;
            }
            else
            {
                missingCount++;
            }
        }

        _output.WriteLine($"refEqual={refEqualCount}, pathEqual={pathEqualCount}, missing={missingCount}, totalDocs={docs.Length}");

        var baseCompilation = AttributionHelpers.StripGeneratedTrees(compilation, docs);
        var remainingGenerated = 0;
        foreach (var doc in docs)
        {
            var docTree = await doc.GetSyntaxTreeAsync();
            if (docTree is not null &&
                baseCompilation.SyntaxTrees.Any(t => AttributionHelpers.TreesMatch(t, docTree)))
            {
                remainingGenerated++;
            }
        }

        _output.WriteLine($"remainingGeneratedAfterStrip={remainingGenerated}");
        Assert.Equal(0, remainingGenerated);

        // Base compilation should still have handwritten trees.
        Assert.Contains(baseCompilation.SyntaxTrees, t =>
            t.FilePath is not null &&
            t.FilePath.EndsWith("PersonViewModel.cs", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class Q3_DriverEquivalenceTests
{
    private readonly ITestOutputHelper _output;

    public Q3_DriverEquivalenceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Self_built_driver_outputs_reconcile_with_workspace_generated_documents()
    {
        await using var session = await WorkspaceSession.OpenAsync(FixturePaths.SampleAppProject);
        var (_, workspaceDocs) = await AttributionHelpers.GetCompilationAndGeneratedDocsAsync(session.Project);
        var (_, runResult, generators) = await AttributionHelpers.RunDriverOnBaseAsync(session.Project);

        _output.WriteLine($"Analyzer generators discovered: {generators.Length}");
        foreach (var g in generators)
        {
            _output.WriteLine($"  {g.AssemblyName}::{g.TypeName}");
        }

        var driverHints = runResult.Results
            .SelectMany(r => r.GeneratedSources.Select(s => (
                Hint: s.HintName,
                Text: s.SourceText.ToString(),
                Generator: r.Generator.GetGeneratorType().FullName)))
            .ToList();

        var workspaceHints = new List<(string Hint, string Text)>();
        foreach (var doc in workspaceDocs)
        {
            var text = await doc.GetTextAsync();
            workspaceHints.Add((doc.HintName, text.ToString()));
        }

        _output.WriteLine($"workspaceDocs={workspaceHints.Count}, driverSources={driverHints.Count}");

        var matchedByContent = 0;
        var matchedByHintOnly = 0;
        foreach (var w in workspaceHints)
        {
            if (driverHints.Any(d => d.Text == w.Text))
            {
                matchedByContent++;
            }
            else if (driverHints.Any(d => d.Hint == w.Hint))
            {
                matchedByHintOnly++;
            }
            else
            {
                _output.WriteLine($"UNMATCHED workspace HintName={w.Hint}, textLen={w.Text.Length}");
            }
        }

        _output.WriteLine($"matchedByContent={matchedByContent}, matchedByHintOnly={matchedByHintOnly}");

        Assert.Equal(workspaceHints.Count, matchedByContent);
        Assert.Equal(0, matchedByHintOnly);

        Assert.Contains(generators, g =>
            g.TypeName.Contains("ObservablePropertyGenerator", StringComparison.Ordinal) ||
            g.AssemblyName.Contains("CommunityToolkit.Mvvm", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class Q4_HintNameCollisionTests
{
    private readonly ITestOutputHelper _output;

    public Q4_HintNameCollisionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Identical_HintName_from_two_generators_does_not_collapse_attribution()
    {
        await using var session = await WorkspaceSession.OpenAsync(FixturePaths.CollisionHostProject);
        var (_, docs) = await AttributionHelpers.GetCompilationAndGeneratedDocsAsync(session.Project);
        var (_, runResult, _) = await AttributionHelpers.RunDriverOnBaseAsync(session.Project);

        _output.WriteLine(AttributionHelpers.DumpGeneratedDocs(docs));

        var sharedHintDocs = docs.Where(d =>
            d.HintName.Contains("SharedHint", StringComparison.OrdinalIgnoreCase)).ToList();

        _output.WriteLine($"workspace docs with SharedHint in HintName: {sharedHintDocs.Count}");
        foreach (var d in sharedHintDocs)
        {
            var text = await d.GetTextAsync();
            _output.WriteLine($"  HintName={d.HintName}; Name={d.Name}; FilePath={d.FilePath}; preview={text.ToString()[..Math.Min(80, text.Length)]}");
        }

        var driverShared = runResult.Results
            .SelectMany(r => r.GeneratedSources.Select(s => (s.HintName, Generator: r.Generator.GetGeneratorType().FullName!, Text: s.SourceText.ToString())))
            .Where(x => x.HintName.Contains("SharedHint", StringComparison.OrdinalIgnoreCase))
            .ToList();

        _output.WriteLine($"driver SharedHint sources: {driverShared.Count}");
        foreach (var s in driverShared)
        {
            _output.WriteLine($"  {s.Generator} => {s.HintName}");
        }

        // Both generators emit the same HintName — associating solely by HintName is unsafe.
        Assert.True(driverShared.Count >= 2, "Expected both collision generators to emit SharedHint via driver.");
        Assert.True(
            driverShared.Select(s => s.HintName).Distinct(StringComparer.Ordinal).Count() == 1,
            "Expected identical HintName strings from both generators.");

        // Workspace should still surface both outputs somehow (possibly renamed FilePath/Name).
        Assert.True(docs.Length >= 2, "Expected workspace to keep both generated documents.");

        var texts = new List<string>();
        foreach (var d in docs)
        {
            texts.Add((await d.GetTextAsync()).ToString());
        }

        Assert.Contains(texts, t => t.Contains("CollisionA", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("CollisionB", StringComparison.Ordinal));
    }
}
