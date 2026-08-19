using DotNetMcp.Core;
using DotNetMcp.Server;
using Xunit.Abstractions;

namespace S4.Verification;

public sealed class Q3_HandleIdentityTests
{
    private readonly ITestOutputHelper _output;

    public Q3_HandleIdentityTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task old_handle_is_symbol_not_found_after_apply_new_name_resolves()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s4-q3-" + Guid.NewGuid().ToString("N"));
        try
        {
            RenameWorkspace.CopyRenameApp(dir);
            var loaded = RenameWorkspace.LoadHandwritten(dir);
            var host = new RenameApplyHost(loaded);
            var symbols = new SymbolQueryService(new GeneratorQueryService());

            var before = host.Session();
            var resolved = await symbols.ResolveByNameAsync(before, "RenameApp.Widget.Ping");
            Assert.NotNull(resolved.Success);
            var oldHandle = resolved.Success!.Handle;
            _output.WriteLine($"oldHandle={oldHandle}");

            var method = await RenameWorkspace.RequireMethodAsync(before, "RenameApp.Widget", "Ping");
            var preview = await host.PreviewAsync(method, "Pong");
            host.Apply(preview.PreviewId);

            var after = host.Session();
            var oldLookup = await symbols.GetSummaryAsync(after, oldHandle);
            Assert.Null(oldLookup.Success);
            Assert.IsType<SymbolNotFoundError>(oldLookup.Error);
            _output.WriteLine($"oldHandleError={oldLookup.Error!.Code} {oldLookup.Error.Message}");

            var forged = oldHandle[..^1] + (oldHandle[^1] == 'a' ? 'b' : 'a');
            var forgedLookup = await symbols.GetSummaryAsync(after, forged);
            Assert.IsType<InvalidSymbolHandleError>(forgedLookup.Error);
            _output.WriteLine($"forgedError={forgedLookup.Error!.Code} {forgedLookup.Error.Message}");

            var fresh = await symbols.ResolveByNameAsync(after, "RenameApp.Widget.Pong");
            Assert.NotNull(fresh.Success);
            _output.WriteLine($"newHandle={fresh.Success!.Handle}");
            Assert.NotEqual(oldHandle, fresh.Success.Handle);
            Assert.Contains("Pong", fresh.Success.Handle, StringComparison.Ordinal);

            var goneByName = await symbols.ResolveByNameAsync(after, "RenameApp.Widget.Ping");
            Assert.IsType<SymbolNotFoundError>(goneByName.Error);
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
