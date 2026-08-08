using System.Diagnostics;

namespace DotNetMcp.Server;

/// <summary>
/// Single active workspace coordinator (ADR-0003 §1/§4): open returns immediately; status polls.
/// </summary>
public sealed class WorkspaceHost : IAsyncDisposable
{
    private readonly ISolutionLoader _loader;
    private readonly SemaphoreSlim _loadMutex = new(1, 1);
    private readonly object _gate = new();

    private CancellationTokenSource? _loadCts;
    private Task? _loadTask;
    private LoadedSolution? _loaded;
    private long _epoch;
    private string _phase = "idle";
    private int _completedUnits;
    private int _totalUnits;
    private string? _error;
    private IReadOnlyList<string> _warnings = [];
    private readonly Stopwatch _elapsed = new();
    private long _estimatedRemainingMs;

    public WorkspaceHost(ISolutionLoader loader)
    {
        _loader = loader;
    }

    public WorkspaceStatusDto BeginOpen(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        CancelInFlightUnlocked();

        lock (_gate)
        {
            _phase = "loading";
            _completedUnits = 0;
            _totalUnits = 1;
            _error = null;
            _warnings = [];
            _estimatedRemainingMs = 0;
            _elapsed.Restart();
            _loadCts = new CancellationTokenSource();
        }

        var cts = _loadCts!;
        _loadTask = Task.Run(() => RunLoadAsync(path, cts.Token));

        return GetStatus();
    }

    public WorkspaceStatusDto GetStatus()
    {
        lock (_gate)
        {
            return BuildStatusUnlocked();
        }
    }

    public bool TryGetReadySession(out IWorkspaceSession? session)
    {
        lock (_gate)
        {
            if (_phase != "ready" || _loaded is null)
            {
                session = null;
                return false;
            }

            session = new WorkspaceSession(_loaded, _epoch);
            return true;
        }
    }

    private async Task RunLoadAsync(string path, CancellationToken ct)
    {
        await _loadMutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();

            // Dispose previous loaded workspace after acquiring the mutex.
            LoadedSolution? previous;
            lock (_gate)
            {
                previous = _loaded;
                _loaded = null;
            }

            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            var progress = new Progress<LoadProgress>(p =>
            {
                lock (_gate)
                {
                    if (_phase is not "loading")
                    {
                        return;
                    }

                    _completedUnits = p.CompletedUnits;
                    _totalUnits = Math.Max(1, p.TotalUnits);
                    UpdateEstimateUnlocked();
                }
            });

            var loaded = await _loader.OpenAsync(path, progress, ct).ConfigureAwait(false);

            lock (_gate)
            {
                if (ct.IsCancellationRequested)
                {
                    _ = loaded.DisposeAsync();
                    _phase = "cancelled";
                    _elapsed.Stop();
                    _estimatedRemainingMs = 0;
                    return;
                }

                _loaded = loaded;
                _warnings = loaded.Warnings;
                _completedUnits = Math.Max(_completedUnits, loaded.Solution.ProjectIds.Count);
                _totalUnits = Math.Max(1, loaded.Solution.ProjectIds.Count);
                _epoch++;
                _phase = "ready";
                _elapsed.Stop();
                _estimatedRemainingMs = 0;
                _error = null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_gate)
            {
                _phase = "cancelled";
                _elapsed.Stop();
                _estimatedRemainingMs = 0;
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _phase = "failed";
                _error = ex.Message;
                _elapsed.Stop();
                _estimatedRemainingMs = 0;
            }
        }
        finally
        {
            _loadMutex.Release();
        }
    }

    private void CancelInFlightUnlocked()
    {
        CancellationTokenSource? oldCts;
        lock (_gate)
        {
            oldCts = _loadCts;
            _loadCts = null;
            _loadTask = null;
        }

        try
        {
            // Cancel but do not Dispose yet — the in-flight load still holds Token.
            oldCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void UpdateEstimateUnlocked()
    {
        if (_totalUnits <= 0 || _completedUnits >= _totalUnits || !_elapsed.IsRunning)
        {
            _estimatedRemainingMs = 0;
            return;
        }

        if (_completedUnits <= 0 || _elapsed.ElapsedMilliseconds <= 0)
        {
            _estimatedRemainingMs = 0;
            return;
        }

        var perUnit = (double)_elapsed.ElapsedMilliseconds / _completedUnits;
        _estimatedRemainingMs = (long)(perUnit * (_totalUnits - _completedUnits));
    }

    private WorkspaceStatusDto BuildStatusUnlocked()
    {
        var suggested = _phase switch
        {
            "idle" => "Call workspace_open with a .sln / .slnx / .slnf (or project) path under a trusted root.",
            "loading" =>
                "Call workspace_status to poll load progress; do not retry workspace_open.",
            "ready" => "Proceed with query tools such as workspace_list_projects.",
            "failed" => "Inspect error; call workspace_open again with a corrected path if needed.",
            "cancelled" => "Previous load was cancelled; call workspace_open to start a new load.",
            _ => "Call workspace_status."
        };

        return new WorkspaceStatusDto
        {
            Phase = _phase,
            CompletedUnits = _completedUnits,
            TotalUnits = _totalUnits,
            ElapsedMs = _elapsed.ElapsedMilliseconds,
            EstimatedRemainingMs = _estimatedRemainingMs,
            Warnings = _warnings.Count == 0 ? null : _warnings,
            Error = _error,
            SuggestedAction = suggested
        };
    }

    public async ValueTask DisposeAsync()
    {
        CancelInFlightUnlocked();

        if (_loadTask is { } task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // ignored during shutdown
            }
        }

        LoadedSolution? loaded;
        lock (_gate)
        {
            loaded = _loaded;
            _loaded = null;
            _phase = "idle";
        }

        if (loaded is not null)
        {
            await loaded.DisposeAsync().ConfigureAwait(false);
        }

        _loadMutex.Dispose();
    }
}
