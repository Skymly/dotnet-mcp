using DotNetMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

public class IdenticalDocumentTextSeamTests
{
    [Fact]
    public async Task identical_text_does_not_count_as_a_document_change()
    {
        var dir = CreateTempDir();
        try
        {
            var loaded = FakeSolutionLoader.CreateSymbolsLoadedOnDisk(dir);
            var path = Path.Combine(dir, "Calculator.cs");
            var documentId = loaded.Solution.GetDocumentIdsWithFilePath(path).Single();
            var text = (await loaded.Solution.GetDocument(documentId)!.GetTextAsync()).ToString();
            var before = loaded.Solution;

            Assert.False(loaded.TryUpdateDocumentFromText(path, SourceText.From(text)));
            Assert.Same(before, loaded.Solution);

            var changed = text.Replace("Mode = 0;", "Mode = 1;", StringComparison.Ordinal);
            Assert.NotEqual(text, changed);
            Assert.True(loaded.TryUpdateDocumentFromText(path, SourceText.From(changed)));
            var after = (await loaded.Solution.GetDocument(documentId)!.GetTextAsync()).ToString();
            Assert.Contains("Mode = 1;", after, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task identical_disk_text_does_not_advance_epoch()
    {
        var dir = CreateTempDir();
        try
        {
            var projectPath = Path.Combine(dir, "SampleLib.csproj");
            await using var host = new WorkspaceHost(
                FakeSolutionLoader.ImmediateWithSymbolsOnDisk(dir),
                new WorkspaceHostOptions
                {
                    Debounce = TimeSpan.Zero,
                    FileWatcher = new ManualWorkspaceFileWatcher(),
                },
                TrustedRoots.Create([dir]));
            await WorkspaceReady.OpenUntilReadyAsync(host, projectPath);

            Assert.True(host.TryGetReadySession(out var session));
            var typed = Assert.IsType<WorkspaceSession>(session);
            var path = Path.Combine(dir, "Calculator.cs");
            var documentId = typed.Solution.GetDocumentIdsWithFilePath(path).Single();
            var text = (await typed.Solution.GetDocument(documentId)!.GetTextAsync()).ToString();
            var epoch = host.CurrentEpoch;

            await File.WriteAllTextAsync(path, text);
            host.ApplyChangedPaths([path]);

            Assert.Equal(epoch, host.CurrentEpoch);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task apply_identical_text_is_not_reported_as_target_missing()
    {
        var root = CreateTempDir();
        var projectDir = Path.Combine(root, "lib");
        Directory.CreateDirectory(projectDir);
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithSymbolsOnDisk(projectDir));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            Assert.True(fx.WorkspaceHost.TryGetReadySession(out var session));
            var typed = Assert.IsType<WorkspaceSession>(session);
            var path = Path.Combine(projectDir, "Calculator.cs");
            var documentId = typed.Solution.GetDocumentIdsWithFilePath(path).Single();
            var text = (await typed.Solution.GetDocument(documentId)!.GetTextAsync()).ToString();
            Assert.Equal(text, await File.ReadAllTextAsync(path));

            var held = fx.WorkspaceEdit.Preview(new WorkspaceEditDraft(
                WorkspaceEditKind.RenamePreview,
                [new WorkspaceEditDocument(path, text, text)],
                []));
            Assert.False(held.Failed, held.Error?.Message);

            var applied = fx.WorkspaceEdit.Apply(held.Value!.PreviewId, WorkspaceEditKind.RenamePreview);
            Assert.False(applied.Failed, applied.Error?.Message);
            Assert.Equal(text, await File.ReadAllTextAsync(path));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-same-text-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
