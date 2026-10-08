using DotNetMcp.Server;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Tests;

public class WatcherDriftWriteLockSeamTests
{
    [Fact]
    public async Task watcher_apply_does_not_lose_to_a_stale_drift_repair()
    {
        var dir = CreateTempDir();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
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
                    BeforeDriftRepairForTests = () =>
                    {
                        entered.Set();
                        if (!release.Wait(TimeSpan.FromSeconds(10)))
                        {
                            throw new TimeoutException("Drift repair stayed paused.");
                        }
                    }
                },
                TrustedRoots.Create([dir]));
            await WorkspaceReady.OpenUntilReadyAsync(host, projectPath);

            var original = await File.ReadAllTextAsync(path);
            var driftText = original.Replace("Mode = 0;", "Mode = 2;", StringComparison.Ordinal);
            var watcherText = original.Replace("Mode = 0;", "Mode = 3;", StringComparison.Ordinal);
            Assert.NotEqual(original, driftText);
            Assert.NotEqual(driftText, watcherText);
            await File.WriteAllTextAsync(path, driftText);

            var drift = Task.Run(() => host.CheckDrift());
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "Drift did not reach the repair hook.");

            await File.WriteAllTextAsync(path, watcherText);
            var apply = Task.Run(() => host.ApplyChangedPaths([path]));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!apply.IsCompleted
                   && !host.ApplyChangedPathsWaitingForWriteLockForTests
                   && DateTime.UtcNow < deadline)
            {
                await Task.Delay(1);
            }

            Assert.True(
                apply.IsCompleted || host.ApplyChangedPathsWaitingForWriteLockForTests,
                "ApplyChangedPaths neither finished nor waited for the write lock.");
            release.Set();
            await apply.WaitAsync(TimeSpan.FromSeconds(10));
            await drift.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(host.TryGetReadySession(out var session));
            var typed = Assert.IsType<WorkspaceSession>(session);
            var documentId = typed.Solution.GetDocumentIdsWithFilePath(path).Single();
            var workspaceText = (await typed.Solution.GetDocument(documentId)!.GetTextAsync()).ToString();
            Assert.Equal(await File.ReadAllTextAsync(path), workspaceText);
            Assert.Contains("Mode = 3;", workspaceText, StringComparison.Ordinal);
        }
        finally
        {
            release.Set();
            TryDelete(dir);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-drift-lock-" + Guid.NewGuid().ToString("N"));
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
