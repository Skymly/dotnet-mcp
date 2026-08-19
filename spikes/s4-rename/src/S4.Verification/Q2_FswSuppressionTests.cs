using DotNetMcp.Server;
using Microsoft.CodeAnalysis.Text;
using Xunit.Abstractions;

namespace S4.Verification;

public sealed class Q2_FswSuppressionTests
{
    private readonly ITestOutputHelper _output;

    public Q2_FswSuppressionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task suppressed_watcher_events_do_not_advance_epoch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s4-q2s-" + Guid.NewGuid().ToString("N"));
        var watcher = new ManualWorkspaceFileWatcher();
        var suppression = new WriteSuppression();
        try
        {
            RenameWorkspace.CopyRenameApp(dir);
            var sln = Path.Combine(dir, "App.slnx");
            await File.WriteAllTextAsync(sln, "<Solution></Solution>");
            await using var host = new WorkspaceHost(
                new FixtureSolutionLoader(() => RenameWorkspace.LoadHandwritten(dir)),
                new WorkspaceHostOptions
                {
                    Debounce = TimeSpan.Zero,
                    FileWatcher = watcher,
                    WriteSuppression = suppression
                });

            host.BeginOpen(sln);
            await RenameWorkspace.WaitReadyAsync(host);
            var epochBefore = host.CurrentEpoch;
            var widget = Path.Combine(dir, "Widget.cs");
            var next = (await File.ReadAllTextAsync(widget)).Replace("Ping", "Pong", StringComparison.Ordinal);

            using (suppression.Suppress(widget))
            {
                await File.WriteAllTextAsync(widget, next);
                watcher.Raise(widget);
            }

            _output.WriteLine($"epochBefore={epochBefore} epochAfterSuppressedRaise={host.CurrentEpoch}");
            Assert.Equal(epochBefore, host.CurrentEpoch);
            Assert.True(host.TryGetReadySession(out var session) && session is not null);
            var doc = session.Solution.GetDocumentIdsWithFilePath(widget).Single();
            var text = (await session.Solution.GetDocument(doc)!.GetTextAsync()).ToString();
            Assert.Contains("Ping", text, StringComparison.Ordinal);
            Assert.DoesNotContain("public int Pong", text, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task unsuppressed_watcher_events_advance_epoch_once_as_external_drift()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s4-q2u-" + Guid.NewGuid().ToString("N"));
        var watcher = new ManualWorkspaceFileWatcher();
        try
        {
            RenameWorkspace.CopyRenameApp(dir);
            var sln = Path.Combine(dir, "App.slnx");
            await File.WriteAllTextAsync(sln, "<Solution></Solution>");
            await using var host = new WorkspaceHost(
                new FixtureSolutionLoader(() => RenameWorkspace.LoadHandwritten(dir)),
                new WorkspaceHostOptions
                {
                    Debounce = TimeSpan.Zero,
                    FileWatcher = watcher
                });

            host.BeginOpen(sln);
            await RenameWorkspace.WaitReadyAsync(host);
            var epochBefore = host.CurrentEpoch;
            var widget = Path.Combine(dir, "Widget.cs");
            var next = (await File.ReadAllTextAsync(widget)).Replace("Ping", "Pong", StringComparison.Ordinal);
            await File.WriteAllTextAsync(widget, next);
            watcher.Raise(widget);

            _output.WriteLine($"epochBefore={epochBefore} epochAfterUnsuppressedRaise={host.CurrentEpoch}");
            Assert.Equal(epochBefore + 1, host.CurrentEpoch);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task disk_write_without_backfill_looks_like_drift_and_check_drift_advances_epoch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s4-q2d-" + Guid.NewGuid().ToString("N"));
        var watcher = new ManualWorkspaceFileWatcher();
        var suppression = new WriteSuppression();
        try
        {
            RenameWorkspace.CopyRenameApp(dir);
            var sln = Path.Combine(dir, "App.slnx");
            await File.WriteAllTextAsync(sln, "<Solution></Solution>");
            await using var host = new WorkspaceHost(
                new FixtureSolutionLoader(() => RenameWorkspace.LoadHandwritten(dir)),
                new WorkspaceHostOptions
                {
                    Debounce = TimeSpan.Zero,
                    FileWatcher = watcher,
                    WriteSuppression = suppression
                });

            host.BeginOpen(sln);
            await RenameWorkspace.WaitReadyAsync(host);
            var epochBefore = host.CurrentEpoch;
            var widget = Path.Combine(dir, "Widget.cs");
            var next = (await File.ReadAllTextAsync(widget)).Replace("Ping", "Pong", StringComparison.Ordinal);

            using (suppression.Suppress(widget))
            {
                await File.WriteAllTextAsync(widget, next);
                watcher.Raise(widget);
            }

            Assert.Equal(epochBefore, host.CurrentEpoch);
            var drift = host.CheckDrift();
            _output.WriteLine($"epochAfterCheckDrift={drift.Epoch} drifted={drift.Drifted.Count}");
            foreach (var item in drift.Drifted)
            {
                _output.WriteLine($"{item.Kind} repaired={item.Repaired} path={Path.GetFileName(item.Path)}");
            }

            Assert.True(drift.Epoch > epochBefore);
            Assert.Contains(drift.Drifted, d => d.Repaired && d.Path.EndsWith("Widget.cs", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task recommended_apply_backfills_then_suppressed_raise_does_not_double_bump()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s4-q2a-" + Guid.NewGuid().ToString("N"));
        var watcher = new ManualWorkspaceFileWatcher();
        try
        {
            RenameWorkspace.CopyRenameApp(dir);
            var loaded = RenameWorkspace.LoadHandwritten(dir);
            var host = new RenameApplyHost(loaded);
            var session = host.Session();
            var method = await RenameWorkspace.RequireMethodAsync(session, "RenameApp.Widget", "Ping");
            var preview = await host.PreviewAsync(method, "Pong");
            var epochAtPreview = host.Epoch;

            host.Apply(preview.PreviewId, raiseAfterWrite: false, watcher);
            _output.WriteLine($"epochAtPreview={epochAtPreview} epochAfterApply={host.Epoch} files={preview.Documents.Count}");
            Assert.Equal(epochAtPreview + 1, host.Epoch);

            var after = host.Session();
            var widget = Path.Combine(dir, "Widget.cs");
            var doc = after.Solution.GetDocumentIdsWithFilePath(widget).Single();
            var text = (await after.Solution.GetDocument(doc)!.GetTextAsync()).ToString();
            Assert.Contains("Pong", text, StringComparison.Ordinal);
            Assert.Equal(await File.ReadAllTextAsync(widget), text);
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
        }
    }
}
