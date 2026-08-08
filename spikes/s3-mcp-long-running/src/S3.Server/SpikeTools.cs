using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using S3.Core;

namespace S3.Server;

[McpServerToolType]
public sealed class SpikeTools
{
    private readonly SlowJobStore _jobs;
    private readonly ObservationLog _log;

    public SpikeTools(SlowJobStore jobs, ObservationLog log)
    {
        _jobs = jobs;
        _log = log;
    }

    [McpServerTool(Name = "sleep_long"), Description("Blocks for seconds (default 90). Use to observe client tools/call timeouts.")]
    public async Task<string> SleepLong(
        [Description("Seconds to sleep. Default 90.")] int seconds = 90,
        CancellationToken cancellationToken = default)
    {
        seconds = Math.Clamp(seconds, 1, 600);
        _log.Write("sleep_long", "enter", new { seconds, thread = Environment.CurrentManagedThreadId });
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
            _log.Write("sleep_long", "completed", new { seconds });
            return JsonSerializer.Serialize(new { ok = true, sleptSeconds = seconds }, JsonOptions.Default);
        }
        catch (OperationCanceledException)
        {
            _log.Write("sleep_long", "cancelled", new { seconds });
            throw;
        }
    }

    [McpServerTool(Name = "sleep_with_progress"), Description("Sleeps while reporting progress every second. Tests whether progress acts as keepalive.")]
    public async Task<string> SleepWithProgress(
        IProgress<ProgressNotificationValue> progress,
        [Description("Seconds to sleep. Default 90.")] int seconds = 90,
        CancellationToken cancellationToken = default)
    {
        seconds = Math.Clamp(seconds, 1, 600);
        _log.Write("sleep_with_progress", "enter", new { seconds });
        try
        {
            for (var i = 1; i <= seconds; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                progress.Report(new ProgressNotificationValue
                {
                    Progress = i,
                    Total = seconds,
                    Message = $"slept {i}/{seconds}s"
                });
                _log.Write("sleep_with_progress", "tick", new { i, seconds });
            }

            return JsonSerializer.Serialize(new { ok = true, sleptSeconds = seconds, progressReports = seconds }, JsonOptions.Default);
        }
        catch (OperationCanceledException)
        {
            _log.Write("sleep_with_progress", "cancelled", new { seconds });
            throw;
        }
    }

    [McpServerTool(Name = "slow_open"), Description("ADR-0003 manual mode: start a background job and return immediately with jobId + SuggestedAction.")]
    public string SlowOpen(
        [Description("Simulated load duration in seconds. Default 20.")] int seconds = 20,
        [Description("Progress units (granularity). Default 20.")] int units = 20)
    {
        seconds = Math.Clamp(seconds, 1, 600);
        units = Math.Clamp(units, 1, 1000);
        var job = _jobs.Start(TimeSpan.FromSeconds(seconds), units);
        var status = _jobs.Snapshot(job.Id);
        _log.Write("slow_open", "started", status);
        return JsonSerializer.Serialize(status, JsonOptions.Default);
    }

    [McpServerTool(Name = "slow_status"), Description("Poll a job started by slow_open. Follow SuggestedAction; do not retry slow_open while loading.")]
    public string SlowStatus([Description("Job id from slow_open.")] string jobId)
    {
        var status = _jobs.Snapshot(jobId);
        _log.Write("slow_status", "poll", status);
        return JsonSerializer.Serialize(status, JsonOptions.Default);
    }

    [McpServerTool(Name = "concurrent_probe"), Description("Sleeps briefly and returns thread/timing info. Fire multiple calls to verify server-side concurrency.")]
    public async Task<string> ConcurrentProbe(
        [Description("Label to correlate client-side calls.")] string label = "a",
        [Description("Hold time in milliseconds. Default 500.")] int holdMs = 500,
        CancellationToken cancellationToken = default)
    {
        holdMs = Math.Clamp(holdMs, 50, 30_000);
        var started = DateTimeOffset.UtcNow;
        var thread = Environment.CurrentManagedThreadId;
        _log.Write("concurrent_probe", "enter", new { label, thread, started });
        await Task.Delay(holdMs, cancellationToken).ConfigureAwait(false);
        var finished = DateTimeOffset.UtcNow;
        var dto = new ConcurrentProbeResultDto
        {
            Label = label,
            ManagedThreadId = thread,
            StartedAtUtc = started.ToString("O"),
            FinishedAtUtc = finished.ToString("O"),
            OverlapHint = "Compare startedAtUtc across concurrent calls; overlapping intervals imply concurrent handlers."
        };
        _log.Write("concurrent_probe", "exit", dto);
        return JsonSerializer.Serialize(dto, JsonOptions.Default);
    }

    [McpServerTool(Name = "soft_budget_page"), Description("Returns a partial page under a soft time budget with nextCursor (ADR-0003 §3).")]
    public string SoftBudgetPage(
        [Description("Opaque cursor from a previous truncated response.")] string? cursor = null,
        [Description("Max items per page. Default 20.")] int pageSize = 20,
        [Description("Total synthetic items. Default 100.")] int totalItems = 100,
        [Description("Soft budget in milliseconds. Default 25.")] int budgetMs = 25,
        [Description("Simulated cost per item in milliseconds. Default 10.")] int itemCostMs = 10)
    {
        var page = SoftBudgetPager.Page(
            cursor,
            pageSize,
            totalItems,
            TimeSpan.FromMilliseconds(Math.Max(1, budgetMs)),
            TimeSpan.FromMilliseconds(Math.Max(1, itemCostMs)));
        _log.Write("soft_budget_page", "page", page);
        return SoftBudgetPager.ToJson(page);
    }

    [McpServerTool(Name = "dump_observations"), Description("Return spike observation log (concurrency/cancel/progress).")]
    public string DumpObservations()
    {
        var entries = _log.Snapshot();
        return JsonSerializer.Serialize(entries, JsonOptions.Default);
    }
}
