// Q1 — in a dependent F# project's check results, can the defining project's FSharpSymbol
// be matched, and do GetAllUsesOfAllSymbols / GetUsesOfSymbol find the uses?
//
// Route A = in-memory project reference (FSharpProjectOptions.ReferencedProjects +
//           FSharpReferencedProject.NewFSharpReference(outputFile, depOptions)); no dll needed.
// Route B = -r:path/to/dep.dll (dll produced by `dotnet build`, as the product consumes it).

using FSharp.Compiler.CodeAnalysis;
using FSharp.Compiler.Symbols;
using Xunit.Abstractions;

namespace S10.Verification;

public sealed class CrossProjectSymbolTests(ITestOutputHelper output)
{
    private static Lazy<(FixtureProject Lib, FixtureProject App)> Two => Fixtures.Two;

    private static string BuiltLibDll => Fixtures.BuiltTwoLibDll.Value;

    private static FcsHost PublishTwo(out FixtureProject lib, out FixtureProject app)
    {
        (lib, app) = Two.Value;
        var host = new FcsHost();
        foreach (var (path, text) in lib.Files.Concat(app.Files))
        {
            host.Publish(path, text);
        }

        return host;
    }

    private static FSharpSymbol DefiningSymbol(FSharpCheckProjectResults check, string displayName) =>
        check.GetAllUsesOfAllSymbols(null)
            .Where(u => u.IsFromDefinition)
            .Select(u => u.Symbol)
            .First(s => s.DisplayName == displayName);

    private static List<FSharpSymbolUse> UsesNamed(FSharpCheckProjectResults check, string displayName) =>
        check.GetAllUsesOfAllSymbols(null)
            .Where(u => u.Symbol.DisplayName == displayName)
            .ToList();

    /// <summary>Product-style catalog of the dependent project's own declared members.</summary>
    private static List<CatalogItem> CatalogOf(FSharpCheckProjectResults check)
    {
        var items = new List<CatalogItem>();
        void Walk(FSharpEntity entity)
        {
            items.Add(new CatalogItem(
                ProductLogic.EntityFullName(entity), entity.DisplayName, true, []));
            foreach (var member in entity.MembersFunctionsAndValues)
            {
                if (member.IsCompilerGenerated || member.IsPropertyGetterMethod || member.IsPropertySetterMethod)
                {
                    continue;
                }

                items.Add(new CatalogItem(
                    ProductLogic.MemberFullName(entity, member), member.DisplayName, false, []));
            }

            foreach (var nested in entity.NestedEntities)
            {
                Walk(nested);
            }
        }

        foreach (var entity in check.AssemblySignature.Entities)
        {
            Walk(entity);
        }

        return items;
    }

    // ---------- route A ----------

    [Fact]
    public async Task RouteA_uses_resolve_and_symbols_match_via_IsEffectivelySameAs()
    {
        var host = PublishTwo(out var lib, out var app);
        var libOptions = host.OptionsFor(lib);
        var libCheck = await host.CheckAsync(lib, libOptions);
        var libSymbol = DefiningSymbol(libCheck, "doubleThenBump");

        var appOptions = host.OptionsFor(app);
        var appOptionsA = FcsHost.WithReferencedProjects(
            appOptions,
            [FSharpReferencedProject.NewFSharpReference(lib.ReferencedDllPath, libOptions)]);
        var appCheck = await host.CheckAsync(app, appOptionsA);

        Assert.DoesNotContain(appCheck.Diagnostics, d => d.Severity.ToString() == "Error");

        var callUses = UsesNamed(appCheck, "doubleThenBump").Where(u => !u.IsFromDefinition).ToList();
        Assert.NotEmpty(callUses);
        Assert.All(callUses, u => Assert.EndsWith("Use.fs", u.FileName, StringComparison.OrdinalIgnoreCase));

        foreach (var use in callUses)
        {
            output.WriteLine(
                $"use at {use.FileName}:{use.Range.StartLine}:{use.Range.StartColumn} " +
                $"IsEffectivelySameAs(libSymbol)={use.Symbol.IsEffectivelySameAs(libSymbol)} " +
                $"ReferenceEquals={ReferenceEquals(use.Symbol, libSymbol)}");
        }

        // The question the issue asks: does the SAME FSharpSymbol object from the defining
        // project's check match the dependent project's use-site symbol?
        Assert.All(callUses, u => Assert.True(u.Symbol.IsEffectivelySameAs(libSymbol)));
    }

    [Fact]
    public async Task RouteA_GetUsesOfSymbol_finds_uses_in_dependent_project()
    {
        var host = PublishTwo(out var lib, out var app);
        var libOptions = host.OptionsFor(lib);
        var libCheck = await host.CheckAsync(lib, libOptions);
        var libSymbol = DefiningSymbol(libCheck, "doubleThenBump");

        var appOptionsA = FcsHost.WithReferencedProjects(
            host.OptionsFor(app),
            [FSharpReferencedProject.NewFSharpReference(lib.ReferencedDllPath, libOptions)]);
        var appCheck = await host.CheckAsync(app, appOptionsA);

        var uses = appCheck.GetUsesOfSymbol(libSymbol, null);
        output.WriteLine($"GetUsesOfSymbol(libSymbol) in FsApp: {uses.Length} use(s)");
        Assert.NotEmpty(uses);
        Assert.All(uses, u => Assert.EndsWith("Use.fs", u.FileName, StringComparison.OrdinalIgnoreCase));
        Assert.All(uses, u => Assert.False(u.IsFromDefinition));
    }

    // ---------- route B ----------

    [Fact]
    public async Task RouteB_uses_resolve_after_build_and_IsEffectivelySameAs_still_matches()
    {
        var host = PublishTwo(out var lib, out var app);
        var libCheck = await host.CheckAsync(lib, host.OptionsFor(lib));
        var libSymbol = DefiningSymbol(libCheck, "doubleThenBump");

        var dll = BuiltLibDll; // dotnet build output (route B requirement)
        output.WriteLine($"dep dll: {dll}");
        var appB = app with { CompilerReferencePaths = [dll] };

        var appCheck = await host.CheckAsync(appB, host.OptionsFor(appB));

        Assert.DoesNotContain(appCheck.Diagnostics, d => d.Severity.ToString() == "Error");

        var callUses = UsesNamed(appCheck, "doubleThenBump").Where(u => !u.IsFromDefinition).ToList();
        Assert.NotEmpty(callUses);

        foreach (var use in callUses)
        {
            output.WriteLine(
                $"use at {use.Range.StartLine}:{use.Range.StartColumn} " +
                $"IsEffectivelySameAs={use.Symbol.IsEffectivelySameAs(libSymbol)} " +
                $"ReferenceEquals={ReferenceEquals(use.Symbol, libSymbol)} " +
                $"FullName={use.Symbol.FullName}");
        }

        // Surprising but verified: dll-imported symbols are IsEffectivelySameAs-equal to the
        // source-checked defining symbol — identity survives metadata import under FCS 43.
        Assert.All(callUses, u => Assert.True(u.Symbol.IsEffectivelySameAs(libSymbol)));
        Assert.All(callUses, u => Assert.False(ReferenceEquals(u.Symbol, libSymbol)));
    }

    [Fact]
    public async Task RouteB_GetUsesOfSymbol_with_defining_symbol_finds_uses()
    {
        var host = PublishTwo(out var lib, out var app);
        var libCheck = await host.CheckAsync(lib, host.OptionsFor(lib));
        var libSymbol = DefiningSymbol(libCheck, "doubleThenBump");

        var appB = app with { CompilerReferencePaths = [BuiltLibDll] };
        var appCheck = await host.CheckAsync(appB, host.OptionsFor(appB));

        var foreign = appCheck.GetUsesOfSymbol(libSymbol, null);
        output.WriteLine($"GetUsesOfSymbol(lib-side symbol) in FsApp: {foreign.Length} use(s)");
        Assert.NotEmpty(foreign);
        Assert.All(foreign, u => Assert.EndsWith("Use.fs", u.FileName, StringComparison.OrdinalIgnoreCase));

        // Re-resolving in the dependent project (product's catalog path) works equally.
        var libSignature = ProductLogic.SignatureOf(libSymbol);
        output.WriteLine($"lib-side signature: {libSignature}");
        var appSideSymbol = UsesNamed(appCheck, "doubleThenBump")
            .Select(u => u.Symbol)
            .First(s => string.Equals(ProductLogic.SignatureOf(s), libSignature, StringComparison.Ordinal));
        Assert.True(appSideSymbol.IsEffectivelySameAs(libSymbol));
    }

    // ---------- signature matching must work under BOTH routes ----------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Both_routes_signature_match_finds_uses(bool routeA)
    {
        var host = PublishTwo(out var lib, out var app);
        var libOptions = host.OptionsFor(lib);
        var libCheck = await host.CheckAsync(lib, libOptions);
        var libSymbol = DefiningSymbol(libCheck, "doubleThenBump");
        var libSignature = ProductLogic.SignatureOf(libSymbol)!;
        var libItem = new CatalogItem(libSignature, "doubleThenBump", false,
            lib.Files.Select(f => f.Path).ToArray());
        output.WriteLine($"signature: {libSignature} (routeA={routeA})");

        FixtureProject appEffective;
        FSharpProjectOptions appOptions;
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

        // product FindReferencesAsync shape: scan GetAllUsesOfAllSymbols + SameSymbol.
        var hits = appCheck.GetAllUsesOfAllSymbols(null)
            .Where(u => ProductLogic.SameSymbol(u.Symbol, libItem))
            .ToList();

        output.WriteLine($"SameSymbol hits in FsApp: {hits.Count}");
        foreach (var hit in hits)
        {
            output.WriteLine($"  {hit.FileName}:{hit.Range.StartLine}:{hit.Range.StartColumn} def={hit.IsFromDefinition}");
        }

        Assert.NotEmpty(hits.Where(h => !h.IsFromDefinition));
        Assert.All(hits, h => Assert.EndsWith("Use.fs", h.FileName, StringComparison.OrdinalIgnoreCase));
    }

    // ---------- callers + implementations/hierarchy in the dependent project ----------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Both_routes_callers_and_implementations_found_in_dependent_project(bool routeA)
    {
        var host = PublishTwo(out var lib, out var app);
        var libOptions = host.OptionsFor(lib);
        var libCheck = await host.CheckAsync(lib, libOptions);
        var libSymbol = DefiningSymbol(libCheck, "doubleThenBump");
        var libItem = new CatalogItem(
            ProductLogic.SignatureOf(libSymbol)!, "doubleThenBump", false,
            lib.Files.Select(f => f.Path).ToArray());

        FixtureProject appEffective;
        FSharpProjectOptions appOptions;
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
        var uses = appCheck.GetAllUsesOfAllSymbols(null).ToList();
        var definitions = uses.Where(u => u.IsFromDefinition).ToList();
        var catalog = CatalogOf(appCheck);

        // Product order (FindEnclosingDefinition over ALL definitions, then MatchCatalog):
        // the innermost preceding definition is the parameter `n`, which is not a catalog
        // member -> the product heuristic would drop the caller here.
        var productOrder = uses
            .Where(u => !u.IsFromDefinition && ProductLogic.SameSymbol(u.Symbol, libItem))
            .Select(u => ProductLogic.FindEnclosingDefinition(definitions, u, libItem))
            .Where(d => d is not null)
            .Select(d => ProductLogic.SignatureOf(d!.Symbol) ?? d!.Symbol.DisplayName + " (not a catalog member)")
            .ToList();
        output.WriteLine($"product-order enclosing defs: {string.Join(", ", productOrder)}");

        // Catalog-filtered order (recommended): only definitions that are catalog members
        // can enclose a call — `FsApp.Use.doubled` wins.
        var memberDefs = definitions
            .Where(d => catalog.Any(c => ProductLogic.SameSymbol(d.Symbol, c)))
            .ToList();
        var callers = uses
            .Where(u => !u.IsFromDefinition && ProductLogic.SameSymbol(u.Symbol, libItem))
            .Select(u => ProductLogic.FindEnclosingDefinition(memberDefs, u, libItem))
            .Where(d => d is not null)
            .Select(d => ProductLogic.SignatureOf(d!.Symbol) ?? d!.Symbol.DisplayName)
            .ToList();
        output.WriteLine($"catalog-filtered callers of doubleThenBump in FsApp: {string.Join(", ", callers)}");
        Assert.Contains(callers, c => c.Contains("FsApp.Use.doubled", StringComparison.Ordinal));

        // Type hierarchy across projects: Coin implements FsLib.Core.IShape.
        var coin = appCheck.AssemblySignature.Entities
            .SelectMany(e => e.NestedEntities.Concat([e]))
            .FirstOrDefault(e => e.DisplayName == "Coin");
        Assert.NotNull(coin);
        var ifaceNames = coin!.DeclaredInterfaces
            .Where(t => t.HasTypeDefinition)
            .Select(t => ProductLogic.EntityFullName(t.TypeDefinition))
            .ToList();
        output.WriteLine($"Coin interfaces: {string.Join(", ", ifaceNames)}");
        Assert.Contains("FsLib.Core.IShape", ifaceNames);

        // Member of a referenced type: Greeter.Greet use resolves and matches by signature.
        var greetUses = UsesNamed(appCheck, "Greet").Where(u => !u.IsFromDefinition).ToList();
        Assert.NotEmpty(greetUses);
        Assert.All(greetUses, u => Assert.Equal("FsLib.Core.Greeter.Greet(System.String)", ProductLogic.SignatureOf(u.Symbol)));

        foreach (var d in definitions)
        {
            output.WriteLine(
                $"def {d.Symbol.DisplayName} ({d.Symbol.GetType().Name}) {Path.GetFileName(d.FileName)}:" +
                $"{d.Range.StartLine}:{d.Range.StartColumn}-{d.Range.EndLine}:{d.Range.EndColumn}");
        }
    }
}
