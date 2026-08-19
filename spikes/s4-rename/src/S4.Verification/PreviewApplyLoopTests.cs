using DotNetMcp.Core;
using Xunit.Abstractions;

namespace S4.Verification;

public sealed class PreviewApplyLoopTests
{
    private readonly ITestOutputHelper _output;

    public PreviewApplyLoopTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task preview_does_not_write_then_apply_with_suppression_makes_old_symbol_gone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s4-loop-" + Guid.NewGuid().ToString("N"));
        try
        {
            RenameWorkspace.CopyRenameApp(dir);
            var beforeDisk = RenameWorkspace.SnapshotDisk(dir);
            var loaded = RenameWorkspace.LoadHandwritten(dir);
            var host = new RenameApplyHost(loaded);
            var symbols = new SymbolQueryService(new GeneratorQueryService());

            var before = host.Session();
            var old = await symbols.ResolveByNameAsync(before, "RenameApp.Widget.Ping");
            Assert.NotNull(old.Success);
            var method = await RenameWorkspace.RequireMethodAsync(before, "RenameApp.Widget", "Ping");
            var preview = await host.PreviewAsync(method, "Pong");

            Assert.Equal(host.Epoch, preview.Epoch);
            Assert.True(preview.ExpiresAt > DateTimeOffset.UtcNow);
            Assert.Equal(2, preview.Documents.Count);
            Assert.Equal(beforeDisk, RenameWorkspace.SnapshotDisk(dir));
            _output.WriteLine($"previewId={preview.PreviewId} epoch={preview.Epoch} ttlUntil={preview.ExpiresAt:o}");

            host.Apply(preview.PreviewId);

            var afterDisk = RenameWorkspace.SnapshotDisk(dir);
            Assert.NotEqual(beforeDisk, afterDisk);
            Assert.Contains("Pong", afterDisk, StringComparison.Ordinal);
            Assert.DoesNotContain("Ping", afterDisk, StringComparison.Ordinal);

            var after = host.Session();
            var gone = await symbols.GetSummaryAsync(after, old.Success!.Handle);
            Assert.IsType<SymbolNotFoundError>(gone.Error);
            var fresh = await symbols.ResolveByNameAsync(after, "RenameApp.Widget.Pong");
            Assert.NotNull(fresh.Success);
            _output.WriteLine($"applied epoch={host.Epoch} newHandle={fresh.Success!.Handle}");

            var twice = Assert.Throws<InvalidOperationException>(() => host.Apply(preview.PreviewId));
            Assert.Equal("unknown_preview", twice.Message);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task expired_or_epoch_mismatched_preview_is_rejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s4-ttl-" + Guid.NewGuid().ToString("N"));
        try
        {
            RenameWorkspace.CopyRenameApp(dir);
            var loaded = RenameWorkspace.LoadHandwritten(dir);
            var host = new RenameApplyHost(loaded);
            var method = await RenameWorkspace.RequireMethodAsync(host.Session(), "RenameApp.Widget", "Ping");
            var expired = await host.PreviewAsync(method, "Pong", ttl: TimeSpan.FromMilliseconds(-1));
            var ex = Assert.Throws<InvalidOperationException>(() => host.Apply(expired.PreviewId));
            Assert.Equal("preview_expired", ex.Message);
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
