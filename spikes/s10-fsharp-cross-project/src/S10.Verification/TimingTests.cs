// Q3 — check cost on a two-project and a three-project chain, cold vs warm, route A vs B;
// and how a soft budget can gate a sequential cross-project scan.
//
// Numbers are appended to <temp>/s10-fsx/TIMING.txt (also written to test output) so they
// can be pasted into CONCLUSIONS.md. Timings are informative only — assertions cover
// semantics, not wall-clock bounds.

using System.Diagnostics;
using FSharp.Compiler.CodeAnalysis;
using Xunit.Abstractions;

namespace S10.Verification;

public sealed class TimingTests(ITestOutputHelper output)
{
    private static Lazy<(FixtureProject Lib, FixtureProject App)> Two => Fixtures.Two;

    private static Lazy<(FixtureProject Lib, FixtureProject Mid, FixtureProject Top)> Three => Fixtures.Three;

    private static readonly object Sync = new();

    private void Record(string line)
    {
        output.WriteLine(line);
        var path = Path.Combine(Path.GetTempPath(), "s10-fsx", "TIMING.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (Sync)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    private static FcsHost PublishAll(params FixtureProject[] projects)
    {
        var host = new FcsHost();
        foreach (var (path, text) in projects.SelectMany(p => p.Files))
        {
            host.Publish(path, text);
        }

        return host;
    }

    private static async Task<(FSharpCheckProjectResults Check, long Ms)> Timed(
        FcsHost host, FixtureProject project, FSharpProjectOptions options)
    {
        var clock = Stopwatch.StartNew();
        var check = await host.CheckAsync(project, options);
        clock.Stop();
        return (check, clock.ElapsedMilliseconds);
    }

    [Fact]
    public async Task two_project_chain_timing()
    {
        var (lib, app) = Two.Value;

        // Route A, cold checker: the FsLib check is paid once inside FsApp's check.
        {
            var host = PublishAll(lib, app);
            var libOptions = host.OptionsFor(lib);
            var appOptionsA = FcsHost.WithReferencedProjects(
                host.OptionsFor(app),
                [FSharpReferencedProject.NewFSharpReference(lib.ReferencedDllPath, libOptions)]);
            var (appCheck, ms) = await Timed(host, app, appOptionsA);
            Assert.Empty(appCheck.Diagnostics.Where(d => d.Severity.ToString() == "Error"));
            Record($"two/A cold  : app check (FsLib checked in-memory inside) = {ms} ms, " +
                   $"uses={appCheck.GetAllUsesOfAllSymbols(null).Length}");
        }

        // Route A, warm checker: FsLib already checked with the same options instance.
        {
            var host = PublishAll(lib, app);
            var libOptions = host.OptionsFor(lib);
            var (libCheck, libMs) = await Timed(host, lib, libOptions);
            var appOptionsA = FcsHost.WithReferencedProjects(
                host.OptionsFor(app),
                [FSharpReferencedProject.NewFSharpReference(lib.ReferencedDllPath, libOptions)]);
            var (appCheck, appMs) = await Timed(host, app, appOptionsA);
            Record($"two/A warm  : lib={libMs} ms then app={appMs} ms (sum={libMs + appMs} ms)");
        }

        // Route B: real `dotnet build` of FsLib + FsApp check via -r:dll.
        {
            var host = PublishAll(lib, app);
            var preBuilt = File.Exists(lib.OutputDllPath);
            var clock = Stopwatch.StartNew();
            var dll = Fixtures.BuildViaDotNet(lib);
            var buildMs = clock.ElapsedMilliseconds;
            var appB = app with { CompilerReferencePaths = [dll] };
            var (appCheck, appMs) = await Timed(host, appB, host.OptionsFor(appB));
            Assert.Empty(appCheck.Diagnostics.Where(d => d.Severity.ToString() == "Error"));
            Record($"two/B       : dotnet build FsLib={buildMs} ms (incremental={preBuilt}) + app check={appMs} ms (dll={dll})");
        }

        // Route B alternative the product already uses in tests: FCS CompileLibrary instead
        // of msbuild — the dll it emits is identical in kind for -r: purposes.
        {
            var host = PublishAll(lib, app);
            var clock = Stopwatch.StartNew();
            var dll = host.CompileLibrary(
                Path.Combine(Path.GetDirectoryName(lib.ProjectFilePath)!, "bin", "fcs", "FsLib.dll"),
                lib.Files.Select(f => f.Path).ToArray());
            var compileMs = clock.ElapsedMilliseconds;
            var appB = app with { CompilerReferencePaths = [dll] };
            var (appCheck, appMs) = await Timed(host, appB, host.OptionsFor(appB));
            Assert.Empty(appCheck.Diagnostics.Where(d => d.Severity.ToString() == "Error"));
            Record($"two/B(fsc)  : checker.Compile FsLib={compileMs} ms + app check={appMs} ms");
        }
    }

    [Fact]
    public async Task three_project_chain_timing()
    {
        var (lib, mid, top) = Three.Value;
        var host = PublishAll(lib, mid, top);

        var libOptions = host.OptionsFor(lib);
        var (libCheck, libMs) = await Timed(host, lib, libOptions);
        Assert.Empty(libCheck.Diagnostics.Where(d => d.Severity.ToString() == "Error"));

        var midOptions = FcsHost.WithReferencedProjects(
            host.OptionsFor(mid),
            [FSharpReferencedProject.NewFSharpReference(lib.ReferencedDllPath, libOptions)]);
        var (midCheck, midMs) = await Timed(host, mid, midOptions);
        Assert.Empty(midCheck.Diagnostics.Where(d => d.Severity.ToString() == "Error"));

        var topOptions = FcsHost.WithReferencedProjects(
            host.OptionsFor(top),
            [
                FSharpReferencedProject.NewFSharpReference(mid.ReferencedDllPath, midOptions),
                FSharpReferencedProject.NewFSharpReference(lib.ReferencedDllPath, libOptions),
            ]);
        var (topCheck, topMs) = await Timed(host, top, topOptions);
        Assert.Empty(topCheck.Diagnostics.Where(d => d.Severity.ToString() == "Error"));

        // Transitive reachability: FsTop sees both FsMid.Extra.* and FsLib.Core.* uses.
        var names = topCheck.GetAllUsesOfAllSymbols(null).Select(u => u.Symbol.FullName).ToList();
        Assert.Contains("FsMid.Extra.tripleThenBump", names);
        Assert.Contains("FsLib.Core.doubleThenBump", names);

        Record($"three/A warm: lib={libMs} ms, mid={midMs} ms, top={topMs} ms (sum={libMs + midMs + topMs} ms)");

        // Cold variant: single checker, only the top project checked explicitly.
        var cold = PublishAll(lib, mid, top);
        var coldLibOptions = cold.OptionsFor(lib);
        var coldMidOptions = FcsHost.WithReferencedProjects(
            cold.OptionsFor(mid),
            [FSharpReferencedProject.NewFSharpReference(lib.ReferencedDllPath, coldLibOptions)]);
        var coldTopOptions = FcsHost.WithReferencedProjects(
            cold.OptionsFor(top),
            [
                FSharpReferencedProject.NewFSharpReference(mid.ReferencedDllPath, coldMidOptions),
                FSharpReferencedProject.NewFSharpReference(lib.ReferencedDllPath, coldLibOptions),
            ]);
        var clock = Stopwatch.StartNew();
        var coldTopCheck = await cold.CheckAsync(top, coldTopOptions);
        clock.Stop();
        Assert.Empty(coldTopCheck.Diagnostics.Where(d => d.Severity.ToString() == "Error"));
        Record($"three/A cold: top check only (whole graph inside) = {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task soft_budget_gates_sequential_cross_project_scan()
    {
        var (lib, app) = Two.Value;
        var host = PublishAll(lib, app);
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

        int Scan(FSharpCheckProjectResults check, TimeSpan budget, Stopwatch clock, out bool truncated)
        {
            var hits = 0;
            truncated = false;
            foreach (var use in check.GetAllUsesOfAllSymbols(null))
            {
                if (clock.Elapsed >= budget)
                {
                    truncated = true;
                    break;
                }

                if (ProductLogic.SameSymbol(use.Symbol, libItem))
                {
                    hits++;
                }
            }

            return hits;
        }

        // Generous budget: both projects scanned fully.
        var clock = Stopwatch.StartNew();
        var libHits = Scan(libCheck, TimeSpan.FromMinutes(1), clock, out var t1);
        var appHits = Scan(appCheck, TimeSpan.FromMinutes(1), clock, out var t2);
        Record($"budget scan: lib hits={libHits} (truncated={t1}), app hits={appHits} (truncated={t2})");
        Assert.False(t1 || t2);
        Assert.True(appHits >= 1);

        // Zero remaining budget reaching the second project -> truncation flag, no hits.
        var exhausted = new Stopwatch();
        exhausted.Start();
        var appHitsStarved = Scan(appCheck, TimeSpan.Zero, exhausted, out var starved);
        Assert.True(starved);
        Assert.Equal(0, appHitsStarved);
    }
}
