using DotNetMcp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

public class HandwrittenDocumentDiffTests
{
    [Fact]
    public async Task from_document_pairs_drops_source_generator_documents_so_callers_do_not_re_copy_origin_checks()
    {
        using var workspace = CreateWorkspace();
        var project = workspace.CurrentSolution.Projects.Single();
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
            workspace.CurrentSolution,
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
        using var workspace = CreateWorkspace();
        var handwritten = Assert.Single(workspace.CurrentSolution.Projects.Single().Documents);
        var after = workspace.CurrentSolution.WithDocumentText(
            handwritten.Id,
            SourceText.From("namespace GeneratorHost; public static class Host { public static int N => 1; }"));

        var (slices, touchedGenerated) = await HandwrittenDocumentDiff.FromSolutionsAsync(
            workspace.CurrentSolution,
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
        using var workspace = CreateWorkspace();
        var project = workspace.CurrentSolution.Projects.Single();
        var compilation = await project.GetCompilationAsync();
        Assert.NotNull(compilation);
        var type = compilation!.GetTypeByMetadataName("GeneratorHost.PartialThing");
        Assert.NotNull(type);

        var generated = (await project.GetSourceGeneratedDocumentsAsync()).ToArray();
        Assert.NotEmpty(generated);

        var renamed = await Renamer.RenameSymbolAsync(
            workspace.CurrentSolution,
            type!,
            RoslynLanguageAdapter.DefaultRenameOptions,
            "RenamedThing",
            CancellationToken.None);

        var (slices, _) = await HandwrittenDocumentDiff.FromSolutionsAsync(
            workspace.CurrentSolution,
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
        using var workspace = CreateWorkspace();
        var project = workspace.CurrentSolution.Projects.Single();
        var after = workspace.CurrentSolution.AddDocument(
            DocumentId.CreateNewId(project.Id),
            "Extra.cs",
            SourceText.From("namespace GeneratorHost; public class Extra {}"),
            filePath: Path.Combine(Path.GetDirectoryName(project.FilePath) ?? @"C:\fake", "Extra.cs"));

        var (slices, touchedGenerated) = await HandwrittenDocumentDiff.FromSolutionsAsync(
            workspace.CurrentSolution,
            after,
            CancellationToken.None);

        Assert.True(touchedGenerated);
        Assert.Empty(slices);
    }

    private static AdhocWorkspace CreateWorkspace()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        const string projectFilePath = @"C:\fake\GeneratorHost.csproj";

        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "GeneratorHost",
            "GeneratorHost",
            LanguageNames.CSharp,
            filePath: projectFilePath));

        const string source = """
            namespace GeneratorHost;

            public static class Host
            {
                public static string Name => "host";
            }

            public partial class PartialThing
            {
                public string Format() => "hw";
                public string Format(string x) => x;
            }
            """;

        var projectDir = Path.GetDirectoryName(projectFilePath) ?? @"C:\fake";
        solution = solution.AddDocument(
            docId,
            "Host.cs",
            SourceText.From(source),
            filePath: Path.Combine(projectDir, "Host.cs"));
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(
            projectId,
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        solution = solution.AddAnalyzerReference(
            projectId,
            new AnalyzerFileReference(
                typeof(CustomGenerator.MarkerGenerator).Assembly.Location,
                AnalyzerAssemblyLoader.Instance));

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace.");
        }

        return workspace;
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

    private sealed class AnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
    {
        public static AnalyzerAssemblyLoader Instance { get; } = new();

        public void AddDependencyLocation(string fullPath)
        {
        }

        public System.Reflection.Assembly LoadFromPath(string fullPath) =>
            System.Reflection.Assembly.LoadFrom(fullPath);
    }
}
