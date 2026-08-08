using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace S3.Core;

/// <summary>
/// In-memory store for the manual async pattern (ADR-0003 §1): open returns immediately, status polls.
/// </summary>
public sealed class SlowJobStore
{
    private readonly ConcurrentDictionary<string, SlowJob> _jobs = new(StringComparer.Ordinal);

    public SlowJob Start(TimeSpan duration, int totalUnits = 100)
    {
        var job = new SlowJob(Guid.NewGuid().ToString("N"), duration, totalUnits);
        if (!_jobs.TryAdd(job.Id, job))
        {
            throw new InvalidOperationException("Failed to register job.");
        }

        _ = Task.Run(() => job.RunAsync());
        return job;
    }

    public bool TryGet(string id, out SlowJob? job) => _jobs.TryGetValue(id, out job);

    public SlowJobStatusDto Snapshot(string id)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            return SlowJobStatusDto.NotFound(id);
        }

        return job.ToDto();
    }
}

public sealed class SlowJob
{
    private readonly object _gate = new();
    private readonly TimeSpan _duration;
    private readonly int _totalUnits;
    private int _completedUnits;
    private string _phase = "queued";
    private string? _error;
    private DateTimeOffset _startedAt;
    private DateTimeOffset? _finishedAt;

    public SlowJob(string id, TimeSpan duration, int totalUnits)
    {
        Id = id;
        _duration = duration;
        _totalUnits = Math.Max(1, totalUnits);
    }

    public string Id { get; }

    public async Task RunAsync(CancellationToken external = default)
    {
        lock (_gate)
        {
            _phase = "loading";
            _startedAt = DateTimeOffset.UtcNow;
        }

        var slice = TimeSpan.FromMilliseconds(Math.Max(10, _duration.TotalMilliseconds / _totalUnits));
        try
        {
            for (var i = 0; i < _totalUnits; i++)
            {
                await Task.Delay(slice, external).ConfigureAwait(false);
                lock (_gate)
                {
                    _completedUnits = i + 1;
                }
            }

            lock (_gate)
            {
                _phase = "ready";
                _finishedAt = DateTimeOffset.UtcNow;
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                _phase = "cancelled";
                _finishedAt = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _phase = "failed";
                _error = ex.Message;
                _finishedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    public SlowJobStatusDto ToDto()
    {
        lock (_gate)
        {
            var remaining = Math.Max(0, _totalUnits - _completedUnits);
            var elapsed = _finishedAt is { } done
                ? done - _startedAt
                : (_startedAt == default ? TimeSpan.Zero : DateTimeOffset.UtcNow - _startedAt);

            return new SlowJobStatusDto
            {
                JobId = Id,
                Phase = _phase,
                CompletedUnits = _completedUnits,
                TotalUnits = _totalUnits,
                ElapsedMs = (long)elapsed.TotalMilliseconds,
                EstimatedRemainingMs = _phase is "ready" or "cancelled" or "failed"
                    ? 0
                    : (long)(remaining * (_duration.TotalMilliseconds / _totalUnits)),
                Error = _error,
                SuggestedAction = _phase switch
                {
                    "queued" or "loading" => "Call slow_status with this jobId; do not retry slow_open.",
                    "ready" => "Proceed with query tools.",
                    "cancelled" => "Job was cancelled; call slow_open to start again if needed.",
                    "failed" => "Inspect error; call slow_open to start again.",
                    _ => "Call slow_status."
                }
            };
        }
    }
}

public sealed class SlowJobStatusDto
{
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    [JsonPropertyName("phase")]
    public required string Phase { get; init; }

    [JsonPropertyName("completedUnits")]
    public int CompletedUnits { get; init; }

    [JsonPropertyName("totalUnits")]
    public int TotalUnits { get; init; }

    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; init; }

    [JsonPropertyName("estimatedRemainingMs")]
    public long EstimatedRemainingMs { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("suggestedAction")]
    public required string SuggestedAction { get; init; }

    public static SlowJobStatusDto NotFound(string id) => new()
    {
        JobId = id,
        Phase = "not_found",
        SuggestedAction = "Unknown jobId. Call slow_open to start a new job."
    };
}

public sealed class SoftBudgetPageDto
{
    [JsonPropertyName("items")]
    public required IReadOnlyList<string> Items { get; init; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

public sealed class ConcurrentProbeResultDto
{
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("managedThreadId")]
    public int ManagedThreadId { get; init; }

    [JsonPropertyName("startedAtUtc")]
    public required string StartedAtUtc { get; init; }

    [JsonPropertyName("finishedAtUtc")]
    public required string FinishedAtUtc { get; init; }

    [JsonPropertyName("overlapHint")]
    public required string OverlapHint { get; init; }
}
