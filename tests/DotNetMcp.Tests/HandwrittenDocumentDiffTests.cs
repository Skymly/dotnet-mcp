using DotNetMcp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

public class HandwrittenDocumentDiffTests
{
    [Fact]
    public async Task from_document_pairs_drops_source_generator_documents_so_callers_do_not_re_copy_origin_checks()
    {
        var loaded = FakeSolutionLoader.CreateGeneratorsLoaded();
        var project = loaded.Solution.Projects.Single();
        var handwritten = Assert.Single(project.Documents);
        var generated = (await project.GetSourceGeneratedDocumentsAsync())
            .Where(d => !string.IsNullOrWhiteSpace(d.FilePath))
            .ToArray();
        Assert.NotEmpty(generated);

        var pairs = new List<RenameDocumentSlice>
        {
            new(handwritten.FilePath!, "old-hw", "new-hw")
        };
        pairs.AddRange(generated.Select(d => new RenameDocumentSlice(d.FilePath!, "old-gen", "new-gen")));

        var (slices, touchedGenerated) = await HandwrittenDocumentDiff.FromDocumentPairsAsync(
            loaded.Solution,
            pairs,
            CancellationToken.None);

        Assert.True(touchedGenerated);
        var kept = Assert.Single(slices);
        Assert.Equal(handwritten.FilePath, kept.Path);
        Assert.Equal("new-hw", kept.NewText);
        Assert.All(generated, d => Assert.DoesNotContain(slices, s => PathsEqual(s.Path, d.FilePath)));
    }

    [Fact]
    public async Task from_solutions_keeps_handwritten_text_changes_without_generated_slices()
    {
        var loaded = FakeSolutionLoader.CreateGeneratorsLoaded();
        var handwritten = Assert.Single(loaded.Solution.Projects.Single().Documents);
        var after = loaded.Solution.WithDocumentText(
            handwritten.Id,
            SourceText.From("namespace GeneratorHost; public static class Host { public static int N => 1; }"));

        var (slices, touchedGenerated) = await HandwrittenDocumentDiff.FromSolutionsAsync(
            loaded.Solution,
            after,
            CancellationToken.None);

        Assert.False(touchedGenerated);
        var kept = Assert.Single(slices);
        Assert.True(PathsEqual(kept.Path, handwritten.FilePath));
        Assert.Contains("public static int N => 1", kept.NewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task from_solutions_omits_source_generator_documents_when_rename_would_touch_them()
    {
        var loaded = FakeSolutionLoader.CreateGeneratorsLoaded();
        var project = loaded.Solution.Projects.Single();
        var compilation = await project.GetCompilationAsync();
        Assert.NotNull(compilation);
        var type = compilation!.GetTypeByMetadataName("GeneratorHost.PartialThing");
        Assert.NotNull(type);

        var generated = (await project.GetSourceGeneratedDocumentsAsync()).ToArray();
        Assert.NotEmpty(generated);

        var renamed = await Renamer.RenameSymbolAsync(
            loaded.Solution,
            type!,
            RenamePreviewService.DefaultOptions,
            "RenamedThing",
            CancellationToken.None);

        var (slices, _) = await HandwrittenDocumentDiff.FromSolutionsAsync(
            loaded.Solution,
            renamed,
            CancellationToken.None);

        Assert.Contains(slices, s =>
            s.Path.EndsWith("Host.cs", StringComparison.OrdinalIgnoreCase) &&
            s.NewText.Contains("RenamedThing", StringComparison.Ordinal));
        Assert.All(slices, s =>
            Assert.DoesNotContain(".g.cs", Path.GetFileName(s.Path), StringComparison.OrdinalIgnoreCase));
        Assert.All(generated, d =>
        {
            if (!string.IsNullOrWhiteSpace(d.FilePath))
            {
                Assert.DoesNotContain(slices, s => PathsEqual(s.Path, d.FilePath));
            }
        });
    }

    [Fact]
    public async Task from_solutions_flags_added_documents_without_emitting_them_as_slices()
    {
        var loaded = FakeSolutionLoader.CreateGeneratorsLoaded();
        var project = loaded.Solution.Projects.Single();
        var after = loaded.Solution.AddDocument(
            DocumentId.CreateNewId(project.Id),
            "Extra.cs",
            SourceText.From("namespace GeneratorHost; public class Extra {}"),
            filePath: Path.Combine(Path.GetDirectoryName(project.FilePath) ?? @"C:\fake", "Extra.cs"));

        var (slices, touchedGenerated) = await HandwrittenDocumentDiff.FromSolutionsAsync(
            loaded.Solution,
            after,
            CancellationToken.None);

        Assert.True(touchedGenerated);
        Assert.Empty(slices);
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
    }
}
