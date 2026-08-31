using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class WorkspaceEditTests
{
    [Fact]
    public void preview_inside_trusted_root_succeeds()
    {
        var root = CreateTempDir("root");
        var path = Path.Combine(root, "Widget.cs");
        File.WriteAllText(path, "old");
        var writer = ReadyWriter(path, "old");
        var edits = new WorkspaceEdit(writer, TrustedRoots.Create([root]), TimeProvider.System, TimeSpan.FromMinutes(5));

        try
        {
            var outcome = edits.Preview(Draft(path));
            Assert.False(outcome.Failed, outcome.Error?.Message);
            Assert.NotNull(outcome.Value);
            Assert.False(string.IsNullOrWhiteSpace(outcome.Value!.PreviewId));
            Assert.Equal(WorkspaceEditKind.RenamePreview, outcome.Value.Kind);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void preview_outside_root_is_refused_and_not_stored()
    {
        var root = CreateTempDir("root");
        var outside = CreateTempDir("out");
        var path = Path.Combine(outside, "secret.cs");
        File.WriteAllText(path, "secret");
        var writer = new FakeWriter();
        var edits = new WorkspaceEdit(writer, TrustedRoots.Create([root]), TimeProvider.System, TimeSpan.FromMinutes(5));

        try
        {
            var outcome = edits.Preview(Draft(path));
            Assert.True(outcome.Failed);
            Assert.Equal(PolicyErrorCodes.PreviewPathOutsideTrustedRoots, outcome.Error!.Error);
            var apply = edits.Apply("should-not-exist", WorkspaceEditKind.RenamePreview);
            Assert.Equal(PolicyErrorCodes.PreviewNotFound, apply.Error!.Error);
            Assert.Equal(0, writer.WriteCalls);
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    [Fact]
    public void apply_matching_kind_writes_once()
    {
        var root = CreateTempDir("root");
        var path = Path.Combine(root, "Widget.cs");
        File.WriteAllText(path, "old");
        var writer = ReadyWriter(path, "old");
        var edits = new WorkspaceEdit(writer, TrustedRoots.Create([root]), TimeProvider.System, TimeSpan.FromMinutes(5));

        try
        {
            var held = edits.Preview(Draft(path));
            Assert.False(held.Failed, held.Error?.Message);
            var applied = edits.Apply(held.Value!.PreviewId, WorkspaceEditKind.RenamePreview);
            Assert.False(applied.Failed, applied.Error?.Message);
            Assert.Equal(1, writer.WriteCalls);
            Assert.Equal("new", writer.Texts[path]);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void apply_cross_kind_is_mismatch()
    {
        var root = CreateTempDir("root");
        var path = Path.Combine(root, "Widget.cs");
        File.WriteAllText(path, "old");
        var writer = ReadyWriter(path, "old");
        var edits = new WorkspaceEdit(writer, TrustedRoots.Create([root]), TimeProvider.System, TimeSpan.FromMinutes(5));

        try
        {
            var held = edits.Preview(Draft(path));
            var applied = edits.Apply(held.Value!.PreviewId, WorkspaceEditKind.FixPreview);
            Assert.True(applied.Failed);
            Assert.Equal(PolicyErrorCodes.PreviewKindMismatch, applied.Error!.Error);
            Assert.Equal(0, writer.WriteCalls);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void apply_unknown_id_is_not_found()
    {
        var root = CreateTempDir("root");
        var writer = new FakeWriter();
        var edits = new WorkspaceEdit(writer, TrustedRoots.Create([root]), TimeProvider.System, TimeSpan.FromMinutes(5));

        try
        {
            var applied = edits.Apply("deadbeefdeadbeef", WorkspaceEditKind.RenamePreview);
            Assert.True(applied.Failed);
            Assert.Equal(PolicyErrorCodes.PreviewNotFound, applied.Error!.Error);
            Assert.Equal(0, writer.WriteCalls);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void apply_expired_is_expired()
    {
        var root = CreateTempDir("root");
        var path = Path.Combine(root, "Widget.cs");
        File.WriteAllText(path, "old");
        var writer = ReadyWriter(path, "old");
        var clock = new MutableTime { Now = DateTimeOffset.UnixEpoch };
        var edits = new WorkspaceEdit(writer, TrustedRoots.Create([root]), clock, TimeSpan.FromMinutes(5));

        try
        {
            var held = edits.Preview(Draft(path));
            Assert.False(held.Failed, held.Error?.Message);
            clock.Now = clock.Now.AddMinutes(6);
            var applied = edits.Apply(held.Value!.PreviewId, WorkspaceEditKind.RenamePreview);
            Assert.True(applied.Failed);
            Assert.Equal(PolicyErrorCodes.PreviewExpired, applied.Error!.Error);
            Assert.Equal(0, writer.WriteCalls);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static WorkspaceEditDraft Draft(string path) =>
        new(WorkspaceEditKind.RenamePreview, [new WorkspaceEditDocument(path, "old", "new")], []);

    private static FakeWriter ReadyWriter(string path, string text)
    {
        var writer = new FakeWriter { CurrentEpoch = 1 };
        writer.Existing.Add(path);
        writer.Texts[path] = text;
        return writer;
    }

    private static string CreateTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnet-mcp-we-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class MutableTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeWriter : IWorkspaceEditWriter
    {
        public long CurrentEpoch { get; set; }

        public long Generation { get; set; }

        public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Texts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int WriteCalls { get; private set; }

        public bool PathExists(string path) => Existing.Contains(path);

        public WorkspaceEditOutcome<long> WriteDeclaredPaths(IReadOnlyList<WorkspaceEditDocument> documents)
        {
            WriteCalls++;
            foreach (var document in documents)
            {
                Texts[document.Path] = document.NewText;
            }

            CurrentEpoch++;
            return new WorkspaceEditOutcome<long>(CurrentEpoch, null);
        }
    }
}