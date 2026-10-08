using DotNetMcp.Server;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Tests;

public class EpochFSharpSnapshotSeamTests
{
    [Fact]
    public async Task ready_session_does_not_observe_a_new_epoch_before_the_snapshot_commits()
    {
        var dir = CreateTempDir();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var pauseCommit = 0;
        try
        {
            var projectPath = Path.Combine(dir, "SampleLib.csproj");
            var path = Path.Combine(dir, "Calculator.cs");
            await using var host = new WorkspaceHost(
                FakeSolutionLoader.ImmediateWithSymbolsOnDisk(dir),
                new WorkspaceHostOptions
                {
                    Debounce = TimeSpan.FromMinutes(5),
                    FileWatcher = new ManualWorkspaceFileWatcher(),
                    BeforeFSharpSnapshotCommitForTests = () =>
                    {
                        if (Volatile.Read(ref pauseCommit) == 0)
                        {
                            return;
                        }

                        entered.Set();
                        if (!release.Wait(TimeSpan.FromSeconds(10)))
                        {
                            throw new TimeoutException("F# snapshot commit stayed paused.");
                        }
                    }
                },
                TrustedRoots.Create([dir]));
            await WorkspaceReady.OpenUntilReadyAsync(host, projectPath);
            Volatile.Write(ref pauseCommit, 1);

            Assert.True(host.TryGetReadySession(out var before));
            var beforeSession = Assert.IsType<WorkspaceSession>(before);
            var epochBefore = beforeSession.Epoch;
            Assert.Equal(epochBefore, beforeSession.FSharpSnapshot.Epoch);
            var original = await ReadCalculatorAsync(beforeSession, path);

            var next = original.Replace("Mode = 0;", "Mode = 9;", StringComparison.Ordinal);
            Assert.NotEqual(original, next);
            await File.WriteAllTextAsync(path, next);
            var apply = Task.Run(() => host.ApplyChangedPaths([path]));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "Snapshot commit hook was not reached.");

            Assert.True(host.TryGetReadySession(out var during));
            var duringSession = Assert.IsType<WorkspaceSession>(during);
            Assert.Equal(epochBefore, duringSession.Epoch);
            Assert.Equal(duringSession.Epoch, duringSession.FSharpSnapshot.Epoch);
            Assert.DoesNotContain("Mode = 9;", await ReadCalculatorAsync(duringSession, path), StringComparison.Ordinal);

            release.Set();
            await apply.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(host.TryGetReadySession(out var after));
            var afterSession = Assert.IsType<WorkspaceSession>(after);
            Assert.Equal(epochBefore + 1, afterSession.Epoch);
            Assert.Equal(afterSession.Epoch, afterSession.FSharpSnapshot.Epoch);
            Assert.Contains("Mode = 9;", await ReadCalculatorAsync(afterSession, path), StringComparison.Ordinal);
        }
        finally
        {
            release.Set();
            TryDelete(dir);
        }
    }

    private static async Task<string> ReadCalculatorAsync(WorkspaceSession session, string path)
    {
        var documentId = session.Solution.GetDocumentIdsWithFilePath(path).Single();
        return (await session.Solution.GetDocument(documentId)!.GetTextAsync()).ToString();
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-epoch-fs-" + Guid.NewGuid().ToString("N"));
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
