using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class DisposeWriteLockSeamTests
{
    [Fact]
    public async Task dispose_waits_for_the_write_lock_and_does_not_leak_object_disposed()
    {
        var dir = CreateTempDir();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        try
        {
            var projectPath = Path.Combine(dir, "SampleLib.csproj");
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

            var drift = Task.Run(() => host.CheckDrift());
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "Drift did not reach the write lock.");

            Exception? disposeError = null;
            var dispose = Task.Run(async () =>
            {
                try
                {
                    await host.DisposeAsync();
                }
                catch (Exception ex)
                {
                    disposeError = ex;
                }
            });

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!host.DisposeWaitingForWriteLockForTests
                   && !dispose.IsCompleted
                   && DateTime.UtcNow < deadline)
            {
                await Task.Delay(1);
            }

            Assert.True(
                host.DisposeWaitingForWriteLockForTests,
                "DisposeAsync did not wait for the in-flight write lock.");
            Assert.False(dispose.IsCompleted, "DisposeAsync finished while the write side still held the lock.");

            release.Set();
            var driftResult = await drift.WaitAsync(TimeSpan.FromSeconds(10));
            await dispose.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(disposeError);
            Assert.NotNull(driftResult);
            Assert.True(host.LastWriteReleaseOrderForTests > 0);
            Assert.True(
                host.DisposeWriteAcquireOrderForTests > host.LastWriteReleaseOrderForTests,
                "Dispose acquired the write lock before the holder released it.");

            var afterDrift = host.CheckDrift();
            Assert.Contains("workspace_status", afterDrift.SuggestedAction, StringComparison.OrdinalIgnoreCase);

            var afterWrite = host.WriteDeclaredPaths([]);
            Assert.Equal(PolicyErrorCodes.WorkspaceNotReady, afterWrite.Error?.Error);
        }
        finally
        {
            release.Set();
            TryDelete(dir);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-dispose-lock-" + Guid.NewGuid().ToString("N"));
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
