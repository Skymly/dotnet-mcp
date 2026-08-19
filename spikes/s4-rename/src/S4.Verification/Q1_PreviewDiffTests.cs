using DotNetMcp.Core;
using DotNetMcp.Server;
using Microsoft.CodeAnalysis;
using Xunit.Abstractions;

namespace S4.Verification;

public sealed class Q1_PreviewDiffTests
{
    private readonly ITestOutputHelper _output;

    public Q1_PreviewDiffTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task rename_preview_diffs_declaration_and_reference_without_writing_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s4-q1-" + Guid.NewGuid().ToString("N"));
        try
        {
            RenameWorkspace.CopyRenameApp(dir);
            var beforeDisk = RenameWorkspace.SnapshotDisk(dir);
            var loaded = RenameWorkspace.LoadHandwritten(dir);
            var session = new WorkspaceSession(loaded, epoch: 1);
            var method = await RenameWorkspace.RequireMethodAsync(session, "RenameApp.Widget", "Ping");

            var (renamed, slices, elapsed) = await RenameWorkspace.PreviewRenameAsync(
                session.Solution,
                method,
                "Pong");

            _output.WriteLine($"elapsedMs={elapsed.TotalMilliseconds:F1}");
            _output.WriteLine($"changedDocuments={slices.Count}");
            foreach (var slice in slices)
            {
                _output.WriteLine($"--- {Path.GetFileName(slice.Path)} ---");
                _output.WriteLine("OLD:");
                _output.WriteLine(slice.OldText);
                _output.WriteLine("NEW:");
                _output.WriteLine(slice.NewText);
            }

            var added = renamed.GetChanges(session.Solution)
                .GetProjectChanges()
                .SelectMany(c => c.GetAddedDocuments())
                .ToArray();
            var removed = renamed.GetChanges(session.Solution)
                .GetProjectChanges()
                .SelectMany(c => c.GetRemovedDocuments())
                .ToArray();
            _output.WriteLine($"addedDocuments={added.Length} removedDocuments={removed.Length}");

            Assert.Equal(2, slices.Count);
            Assert.Contains(slices, s => s.Path.EndsWith("Widget.cs", StringComparison.OrdinalIgnoreCase)
                                         && s.OldText.Contains("Ping(")
                                         && s.NewText.Contains("Pong(")
                                         && !s.NewText.Contains("Ping("));
            Assert.Contains(slices, s => s.Path.EndsWith("Caller.cs", StringComparison.OrdinalIgnoreCase)
                                         && s.OldText.Contains("widget.Ping(2)")
                                         && s.NewText.Contains("widget.Pong(2)"));
            Assert.Empty(added);
            Assert.Empty(removed);
            Assert.Equal(beforeDisk, RenameWorkspace.SnapshotDisk(dir));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task generated_trees_are_not_emitted_as_workspace_documents_in_the_diff()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s4-q1g-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            RenameWorkspace.CopyRenameApp(dir);
            var loaded = RenameWorkspace.LoadHandwritten(dir, attachGenerator: true);
            var session = new WorkspaceSession(loaded, epoch: 1);
            var compilation = await session.GetCompilationAsync(session.Solution.Projects.Single().Id);
            var generated = compilation.SyntaxTrees
                .Where(t => t.FilePath.Contains("CustomMarker", StringComparison.Ordinal)
                            || t.FilePath.Contains("Marker.g.cs", StringComparison.OrdinalIgnoreCase)
                            || t.ToString().Contains("CustomMarker"))
                .ToArray();
            _output.WriteLine($"compilationTrees={compilation.SyntaxTrees.Count()} generatedLike={generated.Length}");
            foreach (var tree in compilation.SyntaxTrees)
            {
                _output.WriteLine($"tree path='{tree.FilePath}' generated={generated.Contains(tree)}");
            }

            var method = await RenameWorkspace.RequireMethodAsync(session, "RenameApp.Widget", "Ping");
            var (renamed, slices, _) = await RenameWorkspace.PreviewRenameAsync(session.Solution, method, "Pong");
            var sourceGenerated = await renamed.Projects.Single().GetSourceGeneratedDocumentsAsync();
            _output.WriteLine($"previewSlices={slices.Count} sourceGeneratedDocs={sourceGenerated.Count()}");
            Assert.All(slices, s => Assert.DoesNotContain("g.cs", s.Path, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(slices, s => s.NewText.Contains("CustomMarker", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // temp cleanup is best-effort
        }
    }
}
