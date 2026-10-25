// Q4 — can the product's single-project rename slicing (ToSpan -> IsSimpleIdentifierUse ->
// ApplyReplacements, copied into ProductLogic) be applied unchanged to a dependent
// project's documents when the uses come from that project's own check results?
//
// Replicates BuildRenamePreviewAsync's per-use pipeline, minus the defining-project-only
// guard, under both routes.

using FSharp.Compiler.CodeAnalysis;
using Xunit.Abstractions;

namespace S10.Verification;

public sealed class RenameSliceTests(ITestOutputHelper output)
{
    private static Lazy<(FixtureProject Lib, FixtureProject App)> Two => Fixtures.Two;

    private static string BuiltLibDll => Fixtures.BuiltTwoLibDll.Value;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task dependent_project_use_slices_rewrite_with_existing_rules(bool routeA)
    {
        var host = new FcsHost();
        var (lib, app) = Two.Value;
        foreach (var (path, text) in lib.Files.Concat(app.Files))
        {
            host.Publish(path, text);
        }

        var libOptions = host.OptionsFor(lib);
        var libCheck = await host.CheckAsync(lib, libOptions);
        var libSymbol = libCheck.GetAllUsesOfAllSymbols(null)
            .Where(u => u.IsFromDefinition)
            .Select(u => u.Symbol)
            .First(s => s.DisplayName == "doubleThenBump");
        var libItem = new CatalogItem(
            ProductLogic.SignatureOf(libSymbol)!, "doubleThenBump", false,
            lib.Files.Select(f => f.Path).ToArray());

        FSharpProjectOptions appOptions;
        FixtureProject appEffective;
        if (routeA)
        {
            appEffective = app;
            appOptions = FcsHost.WithReferencedProjects(
                host.OptionsFor(appEffective),
                [FSharpReferencedProject.NewFSharpReference(lib.ReferencedDllPath, libOptions)]);
        }
        else
        {
            appEffective = app with { CompilerReferencePaths = [BuiltLibDll] };
            appOptions = host.OptionsFor(appEffective);
        }

        var appCheck = await host.CheckAsync(appEffective, appOptions);

        // Product pipeline: match uses -> span -> slice check -> replace trailing oldName.
        const string oldName = "doubleThenBump";
        const string newName = "renamedFn";
        var spans = new List<(int Start, int Length)>();
        var appText = app.Files[0].Text;
        foreach (var use in appCheck.GetAllUsesOfAllSymbols(null))
        {
            if (!ProductLogic.SameSymbol(use.Symbol, libItem))
            {
                continue;
            }

            Assert.True(host.TryReadText(use.FileName, out var text));
            var (start, length) = ProductLogic.ToSpan(text, use.Range);
            var slice = text.Substring(start, length);
            output.WriteLine($"use slice (routeA={routeA}): '{slice}' @ {use.Range.StartLine}:{use.Range.StartColumn}");
            Assert.True(ProductLogic.IsSimpleIdentifierUse(slice, oldName));

            var replaceStart = slice.EndsWith(oldName, StringComparison.Ordinal)
                ? start + slice.Length - oldName.Length
                : start;
            spans.Add((replaceStart, oldName.Length));
        }

        Assert.NotEmpty(spans);
        var rewritten = ProductLogic.ApplyReplacements(appText, spans, newName);
        output.WriteLine("rewritten Use.fs:\n" + rewritten);

        Assert.DoesNotContain(oldName, rewritten, StringComparison.Ordinal);
        Assert.Contains("renamedFn", rewritten, StringComparison.Ordinal);
        Assert.Contains("FsLib.Core.renamedFn", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public async Task defining_project_and_dependent_project_slices_rewrite_identically()
    {
        var host = new FcsHost();
        var (lib, app) = Two.Value;
        foreach (var (path, text) in lib.Files.Concat(app.Files))
        {
            host.Publish(path, text);
        }

        var libOptions = host.OptionsFor(lib);
        var libCheck = await host.CheckAsync(lib, libOptions);
        var libSymbol = libCheck.GetAllUsesOfAllSymbols(null)
            .Where(u => u.IsFromDefinition)
            .Select(u => u.Symbol)
            .First(s => s.DisplayName == "doubleThenBump");
        var libItem = new CatalogItem(
            ProductLogic.SignatureOf(libSymbol)!, "doubleThenBump", false,
            lib.Files.Select(f => f.Path).ToArray());

        var appOptionsA = FcsHost.WithReferencedProjects(
            host.OptionsFor(app),
            [FSharpReferencedProject.NewFSharpReference(lib.ReferencedDllPath, libOptions)]);
        var appCheck = await host.CheckAsync(app, appOptionsA);

        // Same rule, both docs: def-site slice in Library.fs is the bare identifier,
        // use-site slice in Use.fs may be a qualified tail — both pass IsSimpleIdentifierUse.
        var libText = lib.Files[0].Text;
        var libSpans = SlicesFor(libCheck, libItem, lib.Files[0].Path, libText, "doubleThenBump", output);
        var appText = app.Files[0].Text;
        var appSpans = SlicesFor(appCheck, libItem, app.Files[0].Path, appText, "doubleThenBump", output);

        var newLib = ProductLogic.ApplyReplacements(libText, libSpans, "renamedFn");
        var newApp = ProductLogic.ApplyReplacements(appText, appSpans, "renamedFn");
        Assert.Contains("let renamedFn (n: int)", newLib, StringComparison.Ordinal);
        Assert.Contains("FsLib.Core.renamedFn", newApp, StringComparison.Ordinal);
    }

    private static List<(int Start, int Length)> SlicesFor(
        FSharpCheckProjectResults check,
        CatalogItem item,
        string filePath,
        string text,
        string oldName,
        ITestOutputHelper output)
    {
        var spans = new List<(int Start, int Length)>();
        foreach (var use in check.GetAllUsesOfAllSymbols(null))
        {
            if (!ProductLogic.SameSymbol(use.Symbol, item) ||
                !string.Equals(use.FileName, filePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var (start, length) = ProductLogic.ToSpan(text, use.Range);
            var slice = text.Substring(start, length);
            output.WriteLine($"  slice '{slice}' @ {Path.GetFileName(use.FileName)}:{use.Range.StartLine}:{use.Range.StartColumn}");
            Assert.True(ProductLogic.IsSimpleIdentifierUse(slice, oldName));
            var replaceStart = slice.EndsWith(oldName, StringComparison.Ordinal)
                ? start + slice.Length - oldName.Length
                : start;
            spans.Add((replaceStart, oldName.Length));
        }

        return spans;
    }
}
