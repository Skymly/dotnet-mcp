using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class WatchLostRecoverySeamTests
{
    [Fact]
    public async Task watch_lost_restarts_the_watcher_and_reports_health()
    {
        var root = CreateTempDir();
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        var watcher = new ManualWorkspaceFileWatcher();
        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithSymbolsOnDisk(projectDir),
                new WorkspaceHostOptions
                {
                    Debounce = TimeSpan.FromMinutes(5),
                    FileWatcher = watcher
                });
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);
            var startsAfterOpen = watcher.StartCount;
            Assert.True(startsAfterOpen >= 1);

            watcher.RaiseWatchLost();
            await fx.WorkspaceHost.WatcherRecovery;

            Assert.Equal(startsAfterOpen + 1, watcher.StartCount);
            Assert.True(watcher.IsStarted);
            Assert.Equal("ok", fx.WorkspaceHost.GetStatus().Watcher);
        }
        finally
        {
            watcher.Dispose();
            TryDelete(root);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-watch-lost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
