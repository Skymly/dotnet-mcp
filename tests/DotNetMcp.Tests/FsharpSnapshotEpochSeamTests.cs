using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class FsharpSnapshotEpochSeamTests
{
    [Fact]
    public async Task deleting_fs_file_bumps_epoch_and_drops_it_from_fsharp_snapshot()
    {
        var root = CreateTempDir("del-fs");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        var watcher = new ManualWorkspaceFileWatcher();

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFsharpSymbols(root),
                new WorkspaceHostOptions
                {
                    Debounce = TimeSpan.Zero,
                    FileWatcher = watcher
                });

            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);
            var fsproj = Path.Combine(root, "FsLib", "FsLib.fsproj");
            await File.WriteAllTextAsync(
                fsproj,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"Widget.fs\" /><Compile Include=\"Uses.fs\" /></ItemGroup></Project>");
            Assert.True(fx.WorkspaceHost.TryGetReadySession(out var beforeSession));
            var widgetPath = Path.Combine(root, "FsLib", "Widget.fs");
            Assert.Contains(
                Assert.Single(beforeSession!.FSharpSnapshot.Projects).Documents,
                d => d.Path.Equals(widgetPath, StringComparison.OrdinalIgnoreCase));

            var epochBefore = fx.WorkspaceHost.CurrentEpoch;
            File.Delete(widgetPath);
            watcher.Raise(widgetPath);

            Assert.True(fx.WorkspaceHost.CurrentEpoch > epochBefore);
            Assert.True(fx.WorkspaceHost.TryGetReadySession(out var afterSession));
            Assert.Equal(fx.WorkspaceHost.CurrentEpoch, afterSession!.Epoch);
            Assert.Equal(afterSession.Epoch, afterSession.FSharpSnapshot.Epoch);
            Assert.DoesNotContain(
                Assert.Single(afterSession.FSharpSnapshot.Projects).Documents,
                d => d.Path.Equals(widgetPath, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ready_watcher_recapture_does_not_hand_out_empty_fsharp_snapshot()
    {
        var root = CreateTempDir("no-empty");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        var watcher = new ManualWorkspaceFileWatcher();

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFsharpSymbols(root),
                new WorkspaceHostOptions
                {
                    Debounce = TimeSpan.Zero,
                    FileWatcher = watcher
                });

            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);
            var usesPath = Path.Combine(root, "FsLib", "Uses.fs");
            await File.WriteAllTextAsync(usesPath, """
                module FsLib.Uses

                let go () = Widget.ping()
                let extra () = 1
                """);
            watcher.Raise(usesPath);

            Assert.True(fx.WorkspaceHost.TryGetReadySession(out var session));
            Assert.NotEmpty(Assert.Single(session!.FSharpSnapshot.Projects).Documents);
            Assert.Equal(session.Epoch, session.FSharpSnapshot.Epoch);

            var resolve = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "FsLib.Widget" });
            Assert.True(resolve.IsError is not true, InProcessMcpFixture.TextOf(resolve));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-fs-epoch-" + prefix + "-" + Guid.NewGuid().ToString("N"));
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
        catch
        {
        }
    }
}
