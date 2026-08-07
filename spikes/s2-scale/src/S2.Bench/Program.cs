using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using S2.Core;

namespace S2.Bench;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> Main(string[] args)
    {
        var options = BenchOptions.Parse(args);
        Directory.CreateDirectory(SpikePaths.DataDir);

        Console.WriteLine($"Roslyn packages: Microsoft.CodeAnalysis.* 5.6.0");
        Console.WriteLine($"Solution: {options.SolutionPath}");
        Console.WriteLine($"Mode: {options.Mode}; Cold={options.Cold}");

        if (!File.Exists(options.SolutionPath))
        {
            Console.Error.WriteLine($"Solution not found: {options.SolutionPath}");
            return 2;
        }

        if (options.Cold)
        {
            var root = Path.GetDirectoryName(Path.GetFullPath(options.SolutionPath))!;
            var removed = SolutionLoader.CleanBinObj(root);
            Console.WriteLine($"Cold start: removed {removed} bin/obj directories under {root}");
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var report = new BenchReport
        {
            TimestampUtc = DateTime.UtcNow,
            SolutionPath = options.SolutionPath,
            Cold = options.Cold,
            PackageVersions = new Dictionary<string, string>
            {
                ["Microsoft.CodeAnalysis.CSharp.Workspaces"] = "5.6.0",
                ["Microsoft.CodeAnalysis.Workspaces.MSBuild"] = "5.6.0",
                ["Microsoft.Build.Locator"] = "1.9.1",
            },
        };

        MsBuildBootstrap.EnsureRegistered();
        report.MsBuildPath = MsBuildBootstrap.MsBuildPath;
        report.MsBuildVersion = MsBuildBootstrap.MsBuildVersion;
        report.MsBuildDiscoveryType = MsBuildBootstrap.DiscoveryTypeName;
        Console.WriteLine($"MSBuild: {report.MsBuildVersion} ({report.MsBuildDiscoveryType}) @ {report.MsBuildPath}");

        await using var loaded = await SolutionLoader.OpenSolutionAsync(options.SolutionPath);
        report.Load = new LoadMetrics
        {
            ElapsedMs = loaded.LoadElapsed.TotalMilliseconds,
            PeakWorkingSetMiB = loaded.PeakWorkingSetBytes / (1024.0 * 1024),
            ProjectCount = loaded.Solution.ProjectIds.Count,
            DocumentCount = loaded.Solution.Projects.Sum(p => p.DocumentIds.Count),
            WorkspaceFailedCount = loaded.Diagnostics.Count,
            WorkspaceFailedByKind = loaded.Diagnostics
                .GroupBy(d => d.Kind)
                .ToDictionary(g => g.Key, g => g.Count()),
            SampleFailures = loaded.Diagnostics.Take(30).Select(d => $"[{d.Kind}] {d.Message}").ToList(),
            MultiTfmSamples = DescribeMultiTfm(loaded.Solution),
        };

        Console.WriteLine(
            $"Load: {report.Load.ProjectCount} projects, {report.Load.ElapsedMs:F0} ms, " +
            $"peak WS {report.Load.PeakWorkingSetMiB:F1} MiB, failures {report.Load.WorkspaceFailedCount}");

        if (options.Mode is "load")
        {
            await WriteReportAsync(stamp, "load", report);
            return 0;
        }

        if (options.Mode is "compile" or "all" or "lru" or "findrefs")
        {
            report.Compile = await MeasureCompileAsync(loaded.Solution, options.CompileSample);
            Console.WriteLine(
                $"Compile: n={report.Compile.SampleSize} p50={report.Compile.P50Ms:F0}ms p95={report.Compile.P95Ms:F0}ms " +
                $"full={report.Compile.FullElapsedMs:F0}ms peakWS={report.Compile.PeakWorkingSetMiB:F1}MiB");
        }

        if (options.Mode is "lru" or "all")
        {
            report.Lru = await MeasureLruAsync(loaded.Solution, options.LruCaps);
            foreach (var row in report.Lru)
            {
                Console.WriteLine(
                    $"LRU cap={row.CapacityLabel}: seq={row.SequenceElapsedMs:F0}ms evictions={row.Evictions} peakWS={row.PeakWorkingSetMiB:F1}MiB");
            }
        }

        if (options.Mode is "findrefs" or "all")
        {
            report.FindRefs = await MeasureFindRefsAsync(loaded.Solution);
            if (report.FindRefs is not null)
            {
                foreach (var row in report.FindRefs.Scopes)
                {
                    Console.WriteLine(
                        $"FindRefs {row.Scope}: {row.ElapsedMs:F0}ms refs={row.ReferenceCount} locations={row.LocationCount} docs={row.DocumentCount}");
                }

                foreach (var row in report.FindRefs.LruUnderEntireSolution)
                {
                    Console.WriteLine(
                        $"FindRefs+LRU cap={row.CapacityLabel}: {row.ElapsedMs:F0}ms locs={row.LocationCount} evictions={row.Evictions} peakWS={row.PeakWorkingSetMiB:F1}MiB");
                }
            }
        }

        await WriteReportAsync(stamp, options.Mode, report);
        PrintSoftBudgetHints(report);
        return 0;
    }

    private static List<string> DescribeMultiTfm(Solution solution)
    {
        return solution.Projects
            .Where(p => p.FilePath is not null)
            .GroupBy(p => Path.GetFullPath(p.FilePath!))
            .Where(g => g.Count() > 1)
            .Take(10)
            .Select(g => $"{Path.GetFileName(g.Key)} => {string.Join(", ", g.Select(p => p.Name))}")
            .ToList();
    }

    private static async Task<CompileMetrics> MeasureCompileAsync(Solution solution, int sampleSize)
    {
        var projects = solution.Projects.ToArray();
        var sample = projects
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Take(Math.Min(sampleSize, projects.Length))
            .ToArray();

        var sampleTimings = new List<NamedTiming>(sample.Length);
        using var metrics = ProcessMetrics.Start();
        var fullSw = Stopwatch.StartNew();

        foreach (var project in sample)
        {
            var sw = Stopwatch.StartNew();
            _ = await project.GetCompilationAsync();
            sw.Stop();
            sampleTimings.Add(new NamedTiming { Name = project.Name, ElapsedMs = sw.Elapsed.TotalMilliseconds });
            metrics.Sample();
        }

        // Full compile remaining if sample < all
        foreach (var project in projects.Skip(sample.Length))
        {
            _ = await project.GetCompilationAsync();
            metrics.Sample();
        }

        fullSw.Stop();
        var times = sampleTimings.Select(t => t.ElapsedMs).OrderBy(x => x).ToList();

        return new CompileMetrics
        {
            SampleSize = times.Count,
            ProjectCount = projects.Length,
            P50Ms = Percentile(times, 0.50),
            P95Ms = Percentile(times, 0.95),
            MaxMs = times.Count == 0 ? 0 : times[^1],
            FullElapsedMs = fullSw.Elapsed.TotalMilliseconds,
            PeakWorkingSetMiB = metrics.PeakWorkingSetBytes / (1024.0 * 1024),
            SampleProjectTimesMs = sampleTimings
                .OrderByDescending(t => t.ElapsedMs)
                .Take(15)
                .ToList(),
        };
    }

    private static async Task<List<LruMetrics>> MeasureLruAsync(Solution solution, int[] caps)
    {
        var projects = solution.Projects.OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
        if (projects.Length == 0)
        {
            return [];
        }

        // Pick a stable symbol from the middle project for the query sequence.
        var anchor = projects[Math.Min(projects.Length / 2, projects.Length - 1)];
        var results = new List<LruMetrics>();

        foreach (var cap in caps)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var lru = new CompilationLru(cap);
            using var metrics = ProcessMetrics.Start();
            var sw = Stopwatch.StartNew();

            // Query sequence aligned to issue Q6: goto-def → find-refs → type members → diagnostics
            var comp = await lru.GetOrAddAsync(anchor);
            var type = comp.GetSymbolsWithName(n => n.Length > 3, SymbolFilter.Type)
                .OfType<INamedTypeSymbol>()
                .FirstOrDefault(t => t.Locations.Any(l => l.IsInSource));

            // Fixed touch pattern across caps so eviction/timing comparisons are fair.
            const int touchCount = 80;
            for (var i = 0; i < Math.Min(touchCount, projects.Length); i++)
            {
                await lru.GetOrAddAsync(projects[i]);
            }

            for (var i = 0; i < Math.Min(20, projects.Length); i++)
            {
                await lru.GetOrAddAsync(projects[i]);
            }

            if (type is not null)
            {
                // Goto-def style: resolve source definition after cache pressure
                _ = await SymbolFinder.FindSourceDefinitionAsync(type, solution);
                _ = type.GetMembers().Length;
                _ = await SymbolFinder.FindReferencesAsync(
                    type,
                    solution,
                    FindRefsScopes.DocumentsForScope(solution, anchor, FindRefsScopeKind.DependencyClosure));
            }

            for (var i = 0; i < Math.Min(10, projects.Length); i++)
            {
                var c = await lru.GetOrAddAsync(projects[i]);
                _ = c.GetDiagnostics().Length;
            }

            sw.Stop();
            metrics.Sample();

            results.Add(new LruMetrics
            {
                Capacity = cap,
                CapacityLabel = cap <= 0 ? "unlimited" : cap.ToString(),
                SequenceElapsedMs = sw.Elapsed.TotalMilliseconds,
                Evictions = lru.Evictions,
                FinalCount = lru.Count,
                PeakWorkingSetMiB = metrics.PeakWorkingSetBytes / (1024.0 * 1024),
                AnchorProject = anchor.Name,
                AnchorType = type?.ToDisplayString(),
            });
        }

        return results;
    }

    private static async Task<FindRefsMetrics?> MeasureFindRefsAsync(Solution solution)
    {
        var projects = solution.Projects
            .Where(p => p.Documents.Any())
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();
        if (projects.Length == 0)
        {
            return null;
        }

        // Prefer a widely referenced public type: probe candidates then keep the one with most source locations.
        INamedTypeSymbol? best = null;
        Project? host = null;
        var bestLocations = -1;
        foreach (var project in projects.Where(p => p.Name.Contains("Observables.Core", StringComparison.OrdinalIgnoreCase)
                                                    || p.Name.Contains("Observables.Events", StringComparison.OrdinalIgnoreCase))
                     .Concat(projects)
                     .Take(40))
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is null)
            {
                continue;
            }

            foreach (var candidate in compilation.GetSymbolsWithName(n => n.Length is > 3 and < 40, SymbolFilter.Type)
                         .OfType<INamedTypeSymbol>()
                         .Where(t => t.DeclaredAccessibility == Accessibility.Public &&
                                     t.TypeKind == TypeKind.Class &&
                                     t.Locations.Any(l => l.IsInSource))
                         .Take(8))
            {
                var probe = (await SymbolFinder.FindReferencesAsync(candidate, solution)).ToArray();
                var locs = probe.Sum(r => r.Locations.Count());
                if (locs > bestLocations)
                {
                    bestLocations = locs;
                    best = candidate;
                    host = project;
                }
            }

            if (bestLocations >= 20)
            {
                break;
            }
        }

        if (best is null || host is null)
        {
            Console.WriteLine("FindRefs: no suitable public type found.");
            return null;
        }

        var metrics = new FindRefsMetrics
        {
            Symbol = best.ToDisplayString(),
            HostProject = host.Name,
            Scopes = [],
            LruUnderEntireSolution = [],
        };

        foreach (FindRefsScopeKind scope in Enum.GetValues<FindRefsScopeKind>())
        {
            GC.Collect();
            using var proc = ProcessMetrics.Start();
            var sw = Stopwatch.StartNew();
            var refs = (await FindRefsScopes.FindReferencesAsync(best, solution, host, scope)).ToArray();
            sw.Stop();
            proc.Sample();

            var locCount = refs.Sum(r => r.Locations.Count());
            metrics.Scopes.Add(new FindRefsScopeMetrics
            {
                Scope = scope.ToString(),
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                ReferenceCount = refs.Length,
                LocationCount = locCount,
                DocumentCount = FindRefsScopes.DocumentsForScope(solution, host, scope).Count,
                PeakWorkingSetMiB = proc.PeakWorkingSetBytes / (1024.0 * 1024),
            });
        }

        // Q7: entire-solution find-refs under LRU pressure (pre-touch many projects, then search).
        foreach (var cap in new[] { 10, 50, 0 })
        {
            GC.Collect();
            var lru = new CompilationLru(cap);
            using var proc = ProcessMetrics.Start();
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < Math.Min(60, projects.Length); i++)
            {
                await lru.GetOrAddAsync(projects[i]);
            }

            var refs = (await FindRefsScopes.FindReferencesAsync(
                best, solution, host, FindRefsScopeKind.EntireSolution)).ToArray();
            sw.Stop();
            proc.Sample();
            metrics.LruUnderEntireSolution.Add(new LruFindRefsMetrics
            {
                CapacityLabel = cap <= 0 ? "unlimited" : cap.ToString(),
                Evictions = lru.Evictions,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                LocationCount = refs.Sum(r => r.Locations.Count()),
                PeakWorkingSetMiB = proc.PeakWorkingSetBytes / (1024.0 * 1024),
            });
        }

        return metrics;
    }

    private static void PrintSoftBudgetHints(BenchReport report)
    {
        Console.WriteLine();
        Console.WriteLine("=== Soft budget hints (for ADR-0003) ===");
        if (report.Load is not null)
        {
            Console.WriteLine($"Load wall {report.Load.ElapsedMs:F0}ms → workspace_open must be non-blocking (ADR-0003 §1).");
        }

        if (report.Compile is not null)
        {
            Console.WriteLine($"Compile p95 {report.Compile.P95Ms:F0}ms; full {report.Compile.FullElapsedMs:F0}ms.");
        }

        if (report.FindRefs?.Scopes is { Count: > 0 })
        {
            var entire = report.FindRefs.Scopes.FirstOrDefault(s => s.Scope == nameof(FindRefsScopeKind.EntireSolution));
            if (entire is not null)
            {
                Console.WriteLine($"FindRefs entire-solution {entire.ElapsedMs:F0}ms → soft budget should truncate well below 60s client timeout.");
            }
        }
    }

    private static async Task WriteReportAsync(string stamp, string mode, BenchReport report)
    {
        var path = Path.Combine(SpikePaths.DataDir, $"{stamp}-{mode}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonOptions));
        Console.WriteLine($"Wrote {path}");

        // Also keep a stable latest pointer for CONCLUSIONS.
        var latest = Path.Combine(SpikePaths.DataDir, $"latest-{mode}.json");
        await File.WriteAllTextAsync(latest, JsonSerializer.Serialize(report, JsonOptions));
    }

    private static double Percentile(List<double> sortedAscending, double p)
    {
        if (sortedAscending.Count == 0)
        {
            return 0;
        }

        var idx = (int)Math.Clamp(Math.Ceiling(p * sortedAscending.Count) - 1, 0, sortedAscending.Count - 1);
        return sortedAscending[idx];
    }
}

internal sealed class BenchOptions
{
    public string SolutionPath { get; init; } = SpikePaths.DefaultObservablesSlnx;
    public string Mode { get; init; } = "all";
    public bool Cold { get; init; }
    public int[] LruCaps { get; init; } = [10, 25, 50, 0];
    public int CompileSample { get; init; } = 40;

    public static BenchOptions Parse(string[] args)
    {
        var solution = SpikePaths.DefaultObservablesSlnx;
        var mode = "all";
        var cold = false;
        var lru = new[] { 10, 25, 50, 0 };
        var compileSample = 40;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--solution" when i + 1 < args.Length:
                    solution = args[++i];
                    break;
                case "--mode" when i + 1 < args.Length:
                    mode = args[++i];
                    break;
                case "--cold":
                    cold = true;
                    break;
                case "--lru" when i + 1 < args.Length:
                    lru = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(int.Parse)
                        .ToArray();
                    break;
                case "--compile-sample" when i + 1 < args.Length:
                    compileSample = int.Parse(args[++i]);
                    break;
            }
        }

        return new BenchOptions
        {
            SolutionPath = solution,
            Mode = mode,
            Cold = cold,
            LruCaps = lru,
            CompileSample = compileSample,
        };
    }
}

internal sealed class BenchReport
{
    public DateTime TimestampUtc { get; set; }
    public string SolutionPath { get; set; } = "";
    public bool Cold { get; set; }
    public string? MsBuildPath { get; set; }
    public string? MsBuildVersion { get; set; }
    public string? MsBuildDiscoveryType { get; set; }
    public Dictionary<string, string> PackageVersions { get; set; } = new();
    public LoadMetrics? Load { get; set; }
    public CompileMetrics? Compile { get; set; }
    public List<LruMetrics>? Lru { get; set; }
    public FindRefsMetrics? FindRefs { get; set; }
}

internal sealed class LoadMetrics
{
    public double ElapsedMs { get; set; }
    public double PeakWorkingSetMiB { get; set; }
    public int ProjectCount { get; set; }
    public int DocumentCount { get; set; }
    public int WorkspaceFailedCount { get; set; }
    public Dictionary<string, int> WorkspaceFailedByKind { get; set; } = new();
    public List<string> SampleFailures { get; set; } = [];
    public List<string> MultiTfmSamples { get; set; } = [];
}

internal sealed class CompileMetrics
{
    public int SampleSize { get; set; }
    public int ProjectCount { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double MaxMs { get; set; }
    public double FullElapsedMs { get; set; }
    public double PeakWorkingSetMiB { get; set; }
    public List<NamedTiming> SampleProjectTimesMs { get; set; } = [];
}

internal sealed class NamedTiming
{
    public string Name { get; set; } = "";
    public double ElapsedMs { get; set; }
}

internal sealed class LruMetrics
{
    public int Capacity { get; set; }
    public string CapacityLabel { get; set; } = "";
    public double SequenceElapsedMs { get; set; }
    public int Evictions { get; set; }
    public int FinalCount { get; set; }
    public double PeakWorkingSetMiB { get; set; }
    public string? AnchorProject { get; set; }
    public string? AnchorType { get; set; }
}

internal sealed class FindRefsMetrics
{
    public string Symbol { get; set; } = "";
    public string HostProject { get; set; } = "";
    public List<FindRefsScopeMetrics> Scopes { get; set; } = [];
    public List<LruFindRefsMetrics> LruUnderEntireSolution { get; set; } = [];
}

internal sealed class LruFindRefsMetrics
{
    public string CapacityLabel { get; set; } = "";
    public double ElapsedMs { get; set; }
    public int Evictions { get; set; }
    public int LocationCount { get; set; }
    public double PeakWorkingSetMiB { get; set; }
}

internal sealed class FindRefsScopeMetrics
{
    public string Scope { get; set; } = "";
    public double ElapsedMs { get; set; }
    public int ReferenceCount { get; set; }
    public int LocationCount { get; set; }
    public int DocumentCount { get; set; }
    public double PeakWorkingSetMiB { get; set; }
}
