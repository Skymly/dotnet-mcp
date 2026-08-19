using DotNetMcp.Core;
using DotNetMcp.Server;
using Xunit.Abstractions;

namespace S4.Verification;

public sealed class Q4_GeneratedOriginTests
{
    private readonly ITestOutputHelper _output;

    public Q4_GeneratedOriginTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task source_generator_origin_is_readable_before_renamer_is_invoked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s4-q4-" + Guid.NewGuid().ToString("N"));
        try
        {
            var loaded = RenameWorkspace.LoadGeneratedHost(dir);
            var session = new WorkspaceSession(loaded, epoch: 1);
            var symbols = new SymbolQueryService(new GeneratorQueryService());

            var generated = await symbols.ResolveByNameAsync(session, "SampleApp.Generated.CustomMarker");
            Assert.NotNull(generated.Success);
            var generatedAttr = await symbols.GetAttributionAsync(session, generated.Success!.Handle);
            Assert.NotNull(generatedAttr.Success);
            _output.WriteLine(
                $"generated origin={generatedAttr.Success!.Attribution.OriginKind} generator={generatedAttr.Success.Attribution.Generator?.TypeFullName}");
            Assert.Equal(SymbolOrigin.SourceGenerator, generatedAttr.Success.Attribution.OriginKind);
            Assert.Equal("CustomGenerator.MarkerGenerator", generatedAttr.Success.Attribution.Generator?.TypeFullName);

            var handwritten = await symbols.ResolveByNameAsync(session, "GeneratorHost.Host");
            Assert.NotNull(handwritten.Success);
            var handwrittenAttr = await symbols.GetAttributionAsync(session, handwritten.Success!.Handle);
            Assert.NotNull(handwrittenAttr.Success);
            _output.WriteLine($"handwritten origin={handwrittenAttr.Success!.Attribution.OriginKind}");
            Assert.Equal(SymbolOrigin.Handwritten, handwrittenAttr.Success.Attribution.OriginKind);

            var partial = await symbols.ResolveByNameAsync(session, "GeneratorHost.PartialThing");
            Assert.NotNull(partial.Success);
            var partialAttr = await symbols.GetAttributionAsync(session, partial.Success!.Handle);
            _output.WriteLine($"partial type origin={partialAttr.Success!.Attribution.OriginKind}");
            Assert.Equal(SymbolOrigin.Handwritten, partialAttr.Success.Attribution.OriginKind);
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
