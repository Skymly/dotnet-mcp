using Microsoft.CodeAnalysis;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class WorkspaceEditSeamTests
{
    [Fact]
    public void preview_refuses_outside_root_without_storing()
    {
        var root = CreateTempDir();
        var outside = Path.Combine(Path.GetTempPath(), "dotnet-mcp-we-out-" + Guid.NewGuid().ToString("N") + ".cs");
        var writer = new FakeWorkspaceEditWriter();
        var edits = new WorkspaceEdit(writer, TrustedRoots.Create([root]), TimeProvider.System, TimeSpan.FromMinutes(5));

        try
        {
            var outcome = edits.Preview(new WorkspaceEditDraft(
                WorkspaceEditKind.RenamePreview,
                [new WorkspaceEditDocument(outside, "secret-source", "leaked")],
                []));

            Assert.True(outcome.Failed, outcome.Error?.Error);
            Assert.Equal(PolicyErrorCodes.PreviewPathOutsideTrustedRoots, outcome.Error!.Error);
            Assert.DoesNotContain("secret-source", outcome.Error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("leaked", outcome.Error.SuggestedAction, StringComparison.Ordinal);
            Assert.Empty(writer.Writes);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void apply_rejects_cross_kind_without_writing()
    {
        var root = CreateTempDir();
        var path = Path.Combine(root, "Widget.cs");
        var writer = new FakeWorkspaceEditWriter { CurrentEpoch = 3 };
        writer.Existing.Add(path);
        var edits = new WorkspaceEdit(writer, TrustedRoots.Create([root]), TimeProvider.System, TimeSpan.FromMinutes(5));

        try
        {
            var held = edits.Preview(new WorkspaceEditDraft(
                WorkspaceEditKind.RenamePreview,
                [new WorkspaceEditDocument(path, "old", "new")],
                ["csharp:old"]));
            Assert.False(held.Failed, held.Error?.Message);

            var crossed = edits.Apply(held.Value!.PreviewId, WorkspaceEditKind.FixPreview);
            Assert.True(crossed.Failed);
            Assert.Equal(PolicyErrorCodes.PreviewKindMismatch, crossed.Error!.Error);
            Assert.DoesNotContain("old", crossed.Error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("new", crossed.Error.SuggestedAction, StringComparison.Ordinal);
            Assert.Empty(writer.Writes);

            var applied = edits.Apply(held.Value.PreviewId, WorkspaceEditKind.RenamePreview);
            Assert.False(applied.Failed, applied.Error?.Message);
            Assert.Equal(4, applied.Value!.Epoch);
            Assert.Single(writer.Writes);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void apply_expired_is_distinct_from_unknown_id()
    {
        var root = CreateTempDir();
        var path = Path.Combine(root, "Widget.cs");
        var writer = new FakeWorkspaceEditWriter();
        writer.Existing.Add(path);
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-20T00:00:00Z"));
        var edits = new WorkspaceEdit(writer, TrustedRoots.Create([root]), clock, TimeSpan.FromMinutes(5));

        try
        {
            var held = edits.Preview(new WorkspaceEditDraft(
                WorkspaceEditKind.FixPreview,
                [new WorkspaceEditDocument(path, "old", "new")],
                []));
            Assert.False(held.Failed, held.Error?.Message);

            var unknown = edits.Apply("deadbeefdeadbeef", WorkspaceEditKind.FixPreview);
            Assert.Equal(PolicyErrorCodes.PreviewNotFound, unknown.Error!.Error);

            clock.Advance(TimeSpan.FromMinutes(6));
            var expired = edits.Apply(held.Value!.PreviewId, WorkspaceEditKind.FixPreview);
            Assert.Equal(PolicyErrorCodes.PreviewExpired, expired.Error!.Error);
            Assert.Empty(writer.Writes);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void generation_change_makes_preview_not_found()
    {
        var root = CreateTempDir();
        var path = Path.Combine(root, "Widget.cs");
        var writer = new FakeWorkspaceEditWriter();
        writer.Existing.Add(path);
        var edits = new WorkspaceEdit(
            writer,
            TrustedRoots.Create([root]),
            TimeProvider.System,
            TimeSpan.FromMinutes(5));

        try
        {
            var held = edits.Preview(new WorkspaceEditDraft(
                WorkspaceEditKind.RefactoringPreview,
                [new WorkspaceEditDocument(path, "old", "new")],
                []));
            Assert.False(held.Failed, held.Error?.Message);
            writer.Generation++;

            var missing = edits.Apply(held.Value!.PreviewId, WorkspaceEditKind.RefactoringPreview);
            Assert.Equal(PolicyErrorCodes.PreviewNotFound, missing.Error!.Error);
            Assert.Empty(writer.Writes);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void apply_old_text_mismatch_writes_nothing()
    {
        var root = CreateTempDir();
        var path = Path.Combine(root, "Widget.cs");
        var writer = new FakeWorkspaceEditWriter { CurrentEpoch = 3 };
        writer.Existing.Add(path);
        writer.Texts[path] = "old";
        var edits = new WorkspaceEdit(writer, TrustedRoots.Create([root]), TimeProvider.System, TimeSpan.FromMinutes(5));

        try
        {
            var held = edits.Preview(new WorkspaceEditDraft(
                WorkspaceEditKind.RenamePreview,
                [new WorkspaceEditDocument(path, "old", "new")],
                []));
            Assert.False(held.Failed, held.Error?.Message);
            writer.Texts[path] = "mutated";

            var applied = edits.Apply(held.Value!.PreviewId, WorkspaceEditKind.RenamePreview);
            Assert.True(applied.Failed);
            Assert.Equal(PolicyErrorCodes.PreviewTextMismatch, applied.Error!.Error);
            Assert.DoesNotContain("old", applied.Error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("mutated", applied.Error.SuggestedAction, StringComparison.Ordinal);
            Assert.Empty(writer.Writes);
            Assert.Equal(3, writer.CurrentEpoch);
            Assert.Equal("mutated", writer.Texts[path]);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void apply_not_ready_writes_nothing()
    {
        var root = CreateTempDir();
        var path = Path.Combine(root, "Widget.cs");
        var writer = new FakeWorkspaceEditWriter { CurrentEpoch = 2 };
        writer.Existing.Add(path);
        writer.FailWith = new PolicyErrorDto
        {
            Error = PolicyErrorCodes.WorkspaceNotReady,
            Message = "Workspace is not ready; apply did not write.",
            SuggestedAction = "unused"
        };
        var edits = new WorkspaceEdit(writer, TrustedRoots.Create([root]), TimeProvider.System, TimeSpan.FromMinutes(5));

        try
        {
            var held = edits.Preview(new WorkspaceEditDraft(
                WorkspaceEditKind.FixPreview,
                [new WorkspaceEditDocument(path, "old", "new")],
                []));
            Assert.False(held.Failed, held.Error?.Message);

            var applied = edits.Apply(held.Value!.PreviewId, WorkspaceEditKind.FixPreview);
            Assert.True(applied.Failed);
            Assert.Equal(PolicyErrorCodes.WorkspaceNotReady, applied.Error!.Error);
            Assert.Equal(
                "Call workspace_status until ready, then preview and apply again.",
                applied.Error.SuggestedAction);
            Assert.Empty(writer.Writes);
            Assert.Equal(2, writer.CurrentEpoch);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task apply_same_preview_id_is_single_flight()
    {
        var root = CreateTempDir();
        var path = Path.Combine(root, "Widget.cs");
        var writer = new FakeWorkspaceEditWriter
        {
            CurrentEpoch = 1,
            WriteDelay = TimeSpan.FromMilliseconds(40)
        };
        writer.Existing.Add(path);
        writer.Texts[path] = "old";
        var edits = new WorkspaceEdit(writer, TrustedRoots.Create([root]), TimeProvider.System, TimeSpan.FromMinutes(5));

        try
        {
            var held = edits.Preview(new WorkspaceEditDraft(
                WorkspaceEditKind.RefactoringPreview,
                [new WorkspaceEditDocument(path, "old", "new")],
                []));
            Assert.False(held.Failed, held.Error?.Message);
            var previewId = held.Value!.PreviewId;

            var first = Task.Run(() => edits.Apply(previewId, WorkspaceEditKind.RefactoringPreview));
            var second = Task.Run(() => edits.Apply(previewId, WorkspaceEditKind.RefactoringPreview));
            var outcomes = await Task.WhenAll(first, second);
            var successes = outcomes.Count(static o => !o.Failed);
            Assert.True(successes <= 1);
            Assert.True(writer.Writes.Count <= 1);
            Assert.True(writer.WriteCalls <= 1);
            if (successes == 1)
            {
                Assert.Single(writer.Writes);
                Assert.Equal(2, outcomes.Single(static o => !o.Failed).Value!.Epoch);
                Assert.Equal(
                    PolicyErrorCodes.PreviewNotFound,
                    outcomes.Single(static o => o.Failed).Error!.Error);
            }
            else
            {
                Assert.Empty(writer.Writes);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task write_declared_paths_not_ready_leaves_disk_and_epoch()
    {
        var root = CreateTempDir();
        var path = Path.Combine(root, "Widget.cs");
        await File.WriteAllTextAsync(path, "old");
        await using var host = new WorkspaceHost(
            FakeSolutionLoader.ImmediateWithRenameOnDisk(Path.Combine(root, "lib")),
            WorkspaceHostOptions.Default);

        try
        {
            var epoch = host.CurrentEpoch;
            var outcome = host.WriteDeclaredPaths(
                [new WorkspaceEditDocument(path, "old", "new")]);
            Assert.True(outcome.Failed);
            Assert.Equal(PolicyErrorCodes.WorkspaceNotReady, outcome.Error!.Error);
            Assert.Equal("old", await File.ReadAllTextAsync(path));
            Assert.Equal(epoch, host.CurrentEpoch);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task apply_missing_workspace_document_writes_nothing()
    {
        var root = CreateTempDir();
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        var orphan = Path.Combine(root, "Orphan.cs");
        await File.WriteAllTextAsync(orphan, "old-orphan");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithRenameOnDisk(projectDir));
            await OpenUntilReadyAsync(fx, solution);
            var epoch = fx.WorkspaceHost.CurrentEpoch;
            var held = fx.WorkspaceEdit.Preview(new WorkspaceEditDraft(
                WorkspaceEditKind.RenamePreview,
                [new WorkspaceEditDocument(orphan, "old-orphan", "new-orphan")],
                []));
            Assert.False(held.Failed, held.Error?.Message);

            var applied = fx.WorkspaceEdit.Apply(held.Value!.PreviewId, WorkspaceEditKind.RenamePreview);
            Assert.True(applied.Failed);
            Assert.Equal(PolicyErrorCodes.PreviewTargetMissing, applied.Error!.Error);
            Assert.Equal("old-orphan", await File.ReadAllTextAsync(orphan));
            Assert.Equal(epoch, fx.WorkspaceHost.CurrentEpoch);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task write_declared_paths_success_backfills_and_advances_epoch_once()
    {
        var root = CreateTempDir();
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithRenameOnDisk(projectDir));
            await OpenUntilReadyAsync(fx, solution);
            var widget = Path.Combine(projectDir, "Widget.cs");
            var oldText = await File.ReadAllTextAsync(widget);
            var newText = oldText.Replace("Ping", "Pong", StringComparison.Ordinal);
            var epoch = fx.WorkspaceHost.CurrentEpoch;

            var outcome = fx.WorkspaceHost.WriteDeclaredPaths(
                [new WorkspaceEditDocument(widget, oldText, newText)]);
            Assert.False(outcome.Failed, outcome.Error?.Message);
            Assert.Equal(epoch + 1, outcome.Value);
            Assert.Equal(epoch + 1, fx.WorkspaceHost.CurrentEpoch);
            Assert.Equal(newText, await File.ReadAllTextAsync(widget));
            Assert.True(fx.WorkspaceHost.TryGetReadySession(out var session));
            var documentId = session!.Solution.GetDocumentIdsWithFilePath(widget).Single();
            var snapshot = (await session.Solution.GetDocument(documentId)!.GetTextAsync()).ToString();
            Assert.Equal(newText, snapshot);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task mcp_cross_kind_apply_is_preview_kind_mismatch()
    {
        var root = CreateTempDir();
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithRenameOnDisk(projectDir));

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
            for (var i = 0; i < 80; i++)
            {
                var poll = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
                if (InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(poll).Phase == "ready")
                {
                    break;
                }

                await Task.Delay(25);
            }

            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "RenameApp.Widget.Ping" });
            var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;
            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["newName"] = "Pong"
                });
            Assert.True(preview.IsError is not true, InProcessMcpFixture.TextOf(preview));
            var previewId = InProcessMcpFixture.Deserialize<SymbolPreviewRenameResultDto>(preview).PreviewId;

            var crossed = await fx.Client.CallToolAsync(
                "diagnostics_apply_fix",
                new Dictionary<string, object?> { ["previewId"] = previewId });
            Assert.True(crossed.IsError is true);
            Assert.Equal(
                PolicyErrorCodes.PreviewKindMismatch,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(crossed).Error);

            var stillRename = await fx.Client.CallToolAsync(
                "symbol_apply_rename",
                new Dictionary<string, object?> { ["previewId"] = previewId });
            Assert.True(stillRename.IsError is not true, InProcessMcpFixture.TextOf(stillRename));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static async Task OpenUntilReadyAsync(InProcessMcpFixture fx, string path)
    {
        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = path });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
        for (var i = 0; i < 80; i++)
        {
            var poll = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
            if (InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(poll).Phase == "ready")
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("workspace did not become ready");
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-we-" + Guid.NewGuid().ToString("N"));
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

    private sealed class FakeWorkspaceEditWriter : IWorkspaceEditWriter
    {
        public long CurrentEpoch { get; set; }

        public long Generation { get; set; }

        public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Texts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<IReadOnlyList<WorkspaceEditDocument>> Writes { get; } = [];

        public PolicyErrorDto? FailWith { get; set; }

        public TimeSpan WriteDelay { get; set; }

        public int WriteCalls { get; private set; }

        public bool PathExists(string path) => Existing.Contains(path);

        public WorkspaceEditOutcome<long> WriteDeclaredPaths(IReadOnlyList<WorkspaceEditDocument> documents)
        {
            WriteCalls++;
            if (WriteDelay > TimeSpan.Zero)
            {
                Thread.Sleep(WriteDelay);
            }

            if (FailWith is not null)
            {
                return new WorkspaceEditOutcome<long>(0, FailWith);
            }

            foreach (var document in documents)
            {
                if (Texts.TryGetValue(document.Path, out var current)
                    && !string.Equals(current, document.OldText, StringComparison.Ordinal))
                {
                    return new WorkspaceEditOutcome<long>(
                        0,
                        new PolicyErrorDto
                        {
                            Error = PolicyErrorCodes.PreviewTextMismatch,
                            Message = "A preview document no longer matches OldText; nothing was written.",
                            SuggestedAction = "Call the matching preview tool again on the current snapshot."
                        });
                }
            }

            Writes.Add(documents);
            foreach (var document in documents)
            {
                Texts[document.Path] = document.NewText;
            }

            CurrentEpoch++;
            return new WorkspaceEditOutcome<long>(CurrentEpoch, null);
        }
    }
}
