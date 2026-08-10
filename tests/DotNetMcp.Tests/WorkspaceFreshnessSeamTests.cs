using DotNetMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

public class WorkspaceFreshnessSeamTests
{
    [Fact]
    public async Task session_snapshot_stays_frozen_after_loaded_solution_mutates()
    {
        var dir = CreateTempDir("snap");
        try
        {
            var loaded = FakeSolutionLoader.CreateSymbolsLoadedOnDisk(dir);
            var session = new WorkspaceSession(loaded, epoch: 1);
            var before = session.Solution.GetDocumentIdsWithFilePath(
                Path.Combine(dir, "Calculator.cs")).Single();
            var beforeText = (await session.Solution.GetDocument(before)!.GetTextAsync()).ToString();

            var updated = beforeText.Replace(
                "public void Reset() { Name = \"calc\"; Mode = 0; }",
                "public void Reset() { Name = \"calc\"; Mode = 0; }\n                public int Extra() => 1;",
                StringComparison.Ordinal);
            Assert.True(loaded.TryUpdateDocumentFromText(
                Path.Combine(dir, "Calculator.cs"),
                SourceText.From(updated)));

            var afterSessionText = (await session.Solution.GetDocument(before)!.GetTextAsync()).ToString();
            Assert.Equal(beforeText, afterSessionText);
            Assert.DoesNotContain("Extra()", afterSessionText, StringComparison.Ordinal);

            var fresh = new WorkspaceSession(loaded, epoch: 2);
            var freshDoc = fresh.Solution.GetDocumentIdsWithFilePath(
                Path.Combine(dir, "Calculator.cs")).Single();
            var freshText = (await fresh.Solution.GetDocument(freshDoc)!.GetTextAsync()).ToString();
            Assert.Contains("Extra()", freshText, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task source_save_via_watcher_makes_new_symbol_resolvable()
    {
        var root = CreateTempDir("root");
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
                    Debounce = TimeSpan.Zero,
                    FileWatcher = watcher
                });

            await OpenUntilReadyAsync(fx, solution);
            var epochBefore = fx.WorkspaceHost.CurrentEpoch;
            var calcCs = Path.Combine(projectDir, "Calculator.cs");

            var missing = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "SampleLib.Calculator.Extra" });
            Assert.True(missing.IsError is true);

            var source = await File.ReadAllTextAsync(calcCs);
            var next = source.Replace(
                "public void Reset() { Name = \"calc\"; Mode = 0; }",
                "public void Reset() { Name = \"calc\"; Mode = 0; }\n                public int Extra() => 7;",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(calcCs, next);
            watcher.Raise(calcCs);

            Assert.True(fx.WorkspaceHost.CurrentEpoch > epochBefore);

            var found = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "SampleLib.Calculator.Extra" });
            Assert.True(found.IsError is not true, InProcessMcpFixture.TextOf(found));
            var body = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(found);
            Assert.Equal("Extra", body.Summary.DisplayName);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task watcher_epoch_bump_invalidates_member_cursor()
    {
        var root = CreateTempDir("root");
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
                    Debounce = TimeSpan.Zero,
                    FileWatcher = watcher
                });

            await OpenUntilReadyAsync(fx, solution);

            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "SampleLib.Calculator" });
            var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;

            var page1 = await fx.Client.CallToolAsync(
                "symbol_members",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["limit"] = 2
                });
            Assert.True(page1.IsError is not true);
            var pageBody = InProcessMcpFixture.Deserialize<SymbolMembersResultDto>(page1);
            Assert.False(string.IsNullOrWhiteSpace(pageBody.NextCursor));

            var calcCs = Path.Combine(projectDir, "Calculator.cs");
            var source = await File.ReadAllTextAsync(calcCs);
            await File.WriteAllTextAsync(calcCs, source + "\n");
            watcher.Raise(calcCs);

            var page2 = await fx.Client.CallToolAsync(
                "symbol_members",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["limit"] = 2,
                    ["cursor"] = pageBody.NextCursor
                });
            Assert.True(page2.IsError is true);
            var err = InProcessMcpFixture.Deserialize<PolicyErrorDto>(page2);
            Assert.Equal(PolicyErrorCodes.StaleCursor, err.Error);
            Assert.False(string.IsNullOrWhiteSpace(err.SuggestedAction));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task workspace_check_drift_repairs_missed_source_change()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        // No watcher raises — drift path only.
        var watcher = new ManualWorkspaceFileWatcher();

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithSymbolsOnDisk(projectDir),
                new WorkspaceHostOptions
                {
                    Debounce = TimeSpan.Zero,
                    FileWatcher = watcher
                });

            await OpenUntilReadyAsync(fx, solution);
            var epochBefore = fx.WorkspaceHost.CurrentEpoch;
            var calcCs = Path.Combine(projectDir, "Calculator.cs");

            var source = await File.ReadAllTextAsync(calcCs);
            var next = source.Replace(
                "public void Reset() { Name = \"calc\"; Mode = 0; }",
                "public void Reset() { Name = \"calc\"; Mode = 0; }\n                public int Drifted() => 3;",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(calcCs, next);

            var drift = await fx.Client.CallToolAsync(
                "workspace_check_drift",
                new Dictionary<string, object?>());
            Assert.True(drift.IsError is not true, InProcessMcpFixture.TextOf(drift));
            var body = InProcessMcpFixture.Deserialize<WorkspaceCheckDriftResultDto>(drift);
            Assert.Contains(body.Drifted, d => d.Repaired && d.Path.Contains("Calculator.cs", StringComparison.OrdinalIgnoreCase));
            Assert.True(body.Epoch > epochBefore);

            var found = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "SampleLib.Calculator.Drifted" });
            Assert.True(found.IsError is not true, InProcessMcpFixture.TextOf(found));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task write_suppression_skips_epoch_bump_until_released()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        var watcher = new ManualWorkspaceFileWatcher();
        var suppression = new WriteSuppression();

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithSymbolsOnDisk(projectDir),
                new WorkspaceHostOptions
                {
                    Debounce = TimeSpan.Zero,
                    FileWatcher = watcher,
                    WriteSuppression = suppression
                });

            await OpenUntilReadyAsync(fx, solution);
            var epoch = fx.WorkspaceHost.CurrentEpoch;
            var calcCs = Path.Combine(projectDir, "Calculator.cs");

            using (suppression.Suppress(calcCs))
            {
                await File.WriteAllTextAsync(calcCs, await File.ReadAllTextAsync(calcCs) + "\n");
                watcher.Raise(calcCs);
                Assert.Equal(epoch, fx.WorkspaceHost.CurrentEpoch);
            }

            watcher.Raise(calcCs);
            Assert.True(fx.WorkspaceHost.CurrentEpoch > epoch);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task batched_watcher_events_advance_epoch_once()
    {
        var root = CreateTempDir("root");
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
                    Debounce = TimeSpan.Zero,
                    FileWatcher = watcher
                });

            await OpenUntilReadyAsync(fx, solution);
            var epoch = fx.WorkspaceHost.CurrentEpoch;
            var calcCs = Path.Combine(projectDir, "Calculator.cs");
            var genCs = Path.Combine(projectDir, "Generated", "FakeGen", "Calculator.Generated.g.cs");

            await File.WriteAllTextAsync(calcCs, await File.ReadAllTextAsync(calcCs) + "\n");
            await File.WriteAllTextAsync(genCs, await File.ReadAllTextAsync(genCs) + "\n");
            watcher.Raise(calcCs, genCs);

            Assert.Equal(epoch + 1, fx.WorkspaceHost.CurrentEpoch);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task OpenUntilReadyAsync(InProcessMcpFixture fx, string solutionPath)
    {
        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = solutionPath });
        Assert.True(open.IsError is not true);

        for (var i = 0; i < 40; i++)
        {
            var statusResult = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
            var status = InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(statusResult);
            if (status.Phase == "ready")
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("workspace did not become ready");
    }

    private static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-freshness", prefix + "-" + Guid.NewGuid().ToString("N"));
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
            // best-effort cleanup
        }
    }
}
