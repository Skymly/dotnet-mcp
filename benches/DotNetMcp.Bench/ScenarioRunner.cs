using System.Diagnostics;
using System.Text.Json;
using DotNetMcp.Server;

namespace DotNetMcp.Bench;

internal sealed class ScenarioRunner
{
    private readonly BenchOptions _options;
    private readonly BenchReport _report;
    private readonly ProcessSampler _sampler;

    public ScenarioRunner(BenchOptions options, BenchReport report, ProcessSampler sampler)
    {
        _options = options;
        _report = report;
        _sampler = sampler;
    }

    public bool Accepts(string id) =>
        string.IsNullOrWhiteSpace(_options.Filter) ||
        id.Contains(_options.Filter, StringComparison.OrdinalIgnoreCase);

    public async Task<WorkspaceCase?> OpenWorkspaceAsync(
        McpBenchHost host,
        string name,
        string path)
    {
        if (!File.Exists(path))
        {
            _report.Workspaces.Add(new WorkspaceReport
            {
                Name = name,
                Path = path,
                Phase = "missing",
                Error = "path not found",
            });
            return null;
        }

        if (_options.Cold)
        {
            var root = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var removed = WorkspacePrep.CleanBinObj(root);
            if (!_options.JsonOnly)
            {
                Console.WriteLine($"  cold: removed {removed} bin/obj under {root}");
            }
        }

        try
        {
            var (returnMs, status, readyMs) = await WorkspacePrep
                .OpenUntilReadyAsync(host, path, _options.ReadyTimeout)
                .ConfigureAwait(false);

            var list = await host.CallAsync("workspace_list_projects").ConfigureAwait(false);
            var projects = list.IsError is true
                ? []
                : McpBenchHost.Deserialize<WorkspaceListProjectsResultDto>(list).Projects;

            var workspace = new WorkspaceCase
            {
                Name = name,
                Path = path,
                Host = host,
                Status = status,
                Projects = projects,
            };

            _report.Workspaces.Add(new WorkspaceReport
            {
                Name = name,
                Path = path,
                Phase = status.Phase,
                ProjectCount = projects.Count,
                OpenReturnMs = returnMs,
                ReadyMs = readyMs,
            });

            if (Accepts($"{name}.workspace.open.return"))
            {
                RecordOneShot(
                    id: $"{name}.workspace.open.return",
                    tool: "workspace_open",
                    group: "workspace",
                    workspace: name,
                    budgetClass: BudgetClass.OpenReturn,
                    required: true,
                    elapsedMs: returnMs,
                    payloadBytes: 0,
                    resultCount: 1);
            }

            if (Accepts($"{name}.workspace.open.ready"))
            {
                RecordOneShot(
                    id: $"{name}.workspace.open.ready",
                    tool: "workspace_status",
                    group: "workspace",
                    workspace: name,
                    budgetClass: BudgetClass.OpenReady,
                    required: true,
                    elapsedMs: readyMs,
                    payloadBytes: 0,
                    resultCount: projects.Count);
            }

            return workspace;
        }
        catch (Exception ex)
        {
            _report.Workspaces.Add(new WorkspaceReport
            {
                Name = name,
                Path = path,
                Phase = "failed",
                Error = ex.Message,
            });
            return null;
        }
    }

    public async Task MeasureToolAsync(
        WorkspaceCase workspace,
        string id,
        string tool,
        string group,
        string budgetClass,
        IReadOnlyDictionary<string, object?>? args = null,
        bool required = true)
    {
        if (!Accepts(id))
        {
            return;
        }

        var elapsed = new List<double>();
        var payloads = new List<double>();
        var allocated = new List<double>();
        ToolObservation? last = null;
        Exception? failure = null;

        var total = _options.Warmup + _options.Iterations;
        for (var i = 0; i < total; i++)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            try
            {
                var result = await workspace.Host.CallAsync(tool, args).ConfigureAwait(false);
                watch.Stop();
                last = ToolObservation.From(result);
            }
            catch (Exception ex)
            {
                watch.Stop();
                failure = ex;
                break;
            }
            finally
            {
                _sampler.Sample();
            }

            if (i < _options.Warmup)
            {
                continue;
            }

            elapsed.Add(watch.Elapsed.TotalMilliseconds);
            payloads.Add(last?.PayloadBytes ?? 0);
            allocated.Add(Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore));
        }

        AddScenario(
            id,
            tool,
            group,
            workspace.Name,
            required,
            budgetClass,
            elapsed,
            payloads,
            allocated,
            last,
            failure?.Message ?? last?.Error);
    }

    public async Task MeasureParallelAsync(
        WorkspaceCase workspace,
        string id,
        string tool,
        string group,
        string budgetClass,
        Func<int, IReadOnlyDictionary<string, object?>> argsForIndex,
        int parallelism,
        bool required = true)
    {
        if (!Accepts(id))
        {
            return;
        }

        var elapsed = new List<double>();
        var payloads = new List<double>();
        var allocated = new List<double>();
        ToolObservation? last = null;
        Exception? failure = null;

        var total = _options.Warmup + _options.Iterations;
        for (var i = 0; i < total; i++)
        {
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            var watch = Stopwatch.StartNew();
            try
            {
                var tasks = Enumerable.Range(0, parallelism)
                    .Select(index => workspace.Host.CallAsync(tool, argsForIndex(index)))
                    .ToArray();
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                watch.Stop();
                last = ToolObservation.From(results[^1]);
                payloads.Add(results.Sum(r => ToolObservation.From(r).PayloadBytes));
            }
            catch (Exception ex)
            {
                watch.Stop();
                failure = ex;
                break;
            }
            finally
            {
                _sampler.Sample();
            }

            if (i < _options.Warmup)
            {
                if (payloads.Count > 0)
                {
                    payloads.RemoveAt(payloads.Count - 1);
                }

                continue;
            }

            elapsed.Add(watch.Elapsed.TotalMilliseconds);
            allocated.Add(Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore));
        }

        AddScenario(
            id,
            tool,
            group,
            workspace.Name,
            required,
            budgetClass,
            elapsed,
            payloads,
            allocated,
            last,
            failure?.Message ?? last?.Error);
    }

    public void EvaluateGates()
    {
        AddGate(
            "open-nonblocking",
            _report.Scenarios
                .Where(s => s.Id.EndsWith("workspace.open.return", StringComparison.Ordinal))
                .All(s => s.ElapsedMs.P95 < BudgetClass.Milliseconds(BudgetClass.OpenReturn)),
            "workspace_open return p95 must stay under 500ms (ADR-0003 non-blocking).");

        AddGate(
            "under-client-timeout",
            _report.Scenarios.All(s => s.ElapsedMs.P95 < 60_000),
            "No scenario p95 may reach the common 60s tools/call hard top.");

        var requiredErrors = _report.Scenarios
            .Where(s => s.Required && !string.IsNullOrWhiteSpace(s.Error))
            .Select(s => s.Id)
            .ToList();
        AddGate(
            "required-scenarios-ok",
            requiredErrors.Count == 0,
            requiredErrors.Count == 0
                ? "Required scenarios returned no policy/runtime error."
                : "Required scenarios failed: " + string.Join(", ", requiredErrors));

        var overBudget = _report.Scenarios
            .Where(s => s.Required && s.ElapsedMs.P95 > s.BudgetMs)
            .Select(s => s.Id)
            .ToList();
        AddGate(
            "under-soft-budget",
            overBudget.Count == 0,
            overBudget.Count == 0
                ? "Required scenario p95 stayed within the assigned Soft budget."
                : "Over Soft budget: " + string.Join(", ", overBudget));
    }

    public async Task WriteAsync()
    {
        Directory.CreateDirectory(_options.OutDir);
        var stamp = _report.TimestampUtc.ToString("yyyyMMdd-HHmmss");
        var named = Path.Combine(_options.OutDir, $"{stamp}-{_options.Suite}.json");
        var latest = Path.Combine(_options.OutDir, $"latest-{_options.Suite}.json");
        var json = JsonSerializer.Serialize(_report, BenchJsonContext.Default.BenchReport);
        await File.WriteAllTextAsync(named, json).ConfigureAwait(false);
        await File.WriteAllTextAsync(latest, json).ConfigureAwait(false);

        if (!_options.JsonOnly)
        {
            Console.WriteLine();
            Console.WriteLine($"Wrote {named}");
            Console.WriteLine($"Wrote {latest}");
        }
    }

    public void PrintTable()
    {
        if (_options.JsonOnly)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine(
            $"{"id",-52} {"p50",8} {"p95",8} {"max",8} {"n",4} {"pay",7} {"budg",6} {"err"}");
        foreach (var row in _report.Scenarios)
        {
            var err = string.IsNullOrWhiteSpace(row.Error) ? row.BudgetStatus : row.Error;
            Console.WriteLine(
                $"{row.Id,-52} {row.ElapsedMs.P50,8:F1} {row.ElapsedMs.P95,8:F1} {row.ElapsedMs.Max,8:F1} {row.Iterations,4} {row.PayloadBytes?.P50,7:F0} {row.BudgetStatus,6} {err}");
        }

        Console.WriteLine();
        foreach (var gate in _report.Gates)
        {
            Console.WriteLine($"[{gate.Status}] {gate.Id}: {gate.Message}");
        }
    }

    public int ExitCode()
    {
        if (_options.NoGates)
        {
            return 0;
        }

        return _report.Gates.Any(g => g.Status == "fail") ? 1 : 0;
    }

    private void RecordOneShot(
        string id,
        string tool,
        string group,
        string workspace,
        string budgetClass,
        bool required,
        double elapsedMs,
        double payloadBytes,
        int resultCount)
    {
        AddScenario(
            id,
            tool,
            group,
            workspace,
            required,
            budgetClass,
            [elapsedMs],
            [payloadBytes],
            [0],
            new ToolObservation
            {
                IsError = false,
                Payload = "",
                PayloadBytes = (int)payloadBytes,
                ResultCount = resultCount,
            },
            error: null);
    }

    private void AddScenario(
        string id,
        string tool,
        string group,
        string workspace,
        bool required,
        string budgetClass,
        List<double> elapsed,
        List<double> payloads,
        List<double> allocated,
        ToolObservation? last,
        string? error)
    {
        var budgetMs = BudgetClass.Milliseconds(budgetClass);
        var stats = Statistics.From(elapsed);
        var status = BudgetStatus(stats.P95, budgetMs, budgetClass);
        if (!string.IsNullOrWhiteSpace(error) && required)
        {
            status = "fail";
        }

        _report.Scenarios.Add(new ScenarioReport
        {
            Id = id,
            Tool = tool,
            Group = group,
            Workspace = workspace,
            Required = required,
            BudgetClass = budgetClass,
            BudgetMs = budgetMs,
            Iterations = elapsed.Count,
            ElapsedMs = stats,
            PayloadBytes = payloads.Count == 0 ? null : Statistics.From(payloads),
            AllocatedBytes = allocated.Count == 0 ? null : Statistics.From(allocated),
            PeakWorkingSetMiB = _sampler.PeakWorkingSetMiB,
            ResultCount = last?.ResultCount,
            Truncated = last?.Truncated ?? false,
            HasNextCursor = last?.HasNextCursor ?? false,
            Error = error,
            BudgetStatus = status,
        });

        if (!_options.JsonOnly)
        {
            var label = string.IsNullOrWhiteSpace(error) ? status : error;
            Console.WriteLine($"  {id}: p95={stats.P95:F1}ms {label}");
        }
    }

    private static string BudgetStatus(double p95, double budgetMs, string budgetClass)
    {
        if (p95 >= 60_000 && budgetClass != BudgetClass.OpenReady)
        {
            return "fail";
        }

        if (p95 > budgetMs)
        {
            return "fail";
        }

        if (p95 > budgetMs * 0.5)
        {
            return "warn";
        }

        return "pass";
    }

    private void AddGate(string id, bool passed, string message)
    {
        _report.Gates.Add(new GateReport
        {
            Id = id,
            Status = passed ? "pass" : "fail",
            Message = message,
        });
    }
}
