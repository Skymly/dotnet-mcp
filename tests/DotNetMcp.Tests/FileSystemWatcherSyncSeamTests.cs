using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class FileSystemWatcherSyncSeamTests
{
    [Fact]
    public async Task stop_overlapping_start_does_not_leave_an_active_watcher()
    {
        var root = CreateTempDir();
        var watcher = new FileSystemWorkspaceWatcher();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        try
        {
            watcher.BeforeAddingWatcherForTests = () =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Start stayed paused before adding the watcher.");
                }
            };

            var start = Task.Run(() => watcher.Start([root], _ => { }, () => { }));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "Start did not reach the add hook.");

            var startHoldsGate = !Monitor.TryEnter(watcher.SyncForTests);
            if (startHoldsGate)
            {
                var stop = Task.Run(() => watcher.Stop());
                release.Set();
                await start.WaitAsync(TimeSpan.FromSeconds(10));
                await stop.WaitAsync(TimeSpan.FromSeconds(10));
            }
            else
            {
                Monitor.Exit(watcher.SyncForTests);
                watcher.Stop();
                release.Set();
                await start.WaitAsync(TimeSpan.FromSeconds(10));
            }

            Assert.Throws<InvalidOperationException>(() => watcher.RaiseErrorForTests());
        }
        finally
        {
            release.Set();
            watcher.Dispose();
            TryDelete(root);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-watcher-" + Guid.NewGuid().ToString("N"));
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
