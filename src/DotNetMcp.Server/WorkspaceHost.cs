using System.Diagnostics;
using DotNetMcp.Core;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Server;

/// <summary>
/// Single active workspace coordinator (ADR-0003 §1/§4): open returns immediately; status polls.
/// Freshness via internal FSW + debounce + epoch (ADR-0002 §3).
/// </summary>
public sealed class WorkspaceHost : IAsyncDisposable
{
    private readonly ISolutionLoader _loader;
    private readonly WorkspaceHostOptions _options;
    private readonly IWorkspaceFileWatcher _watcher;
    private readonly bool _ownsWatcher;
    private readonly SemaphoreSlim _loadMutex = new(1, 1);
    private readonly object _gate = new();
    private readonly object _debounceGate = new();

    private CancellationTokenSource? _loadCts;
    private Task? _loadTask;
    private LoadedSolution? _loaded;
    private string? _openedPath;
    private long _epoch;
    private string _phase = "idle";
    private int _completedUnits;
    private int _totalUnits;
    private string? _error;
    private IReadOnlyList<string> _warnings = [];
    private readonly Stopwatch _elapsed = new();
    private long _estimatedRemainingMs;

    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _debounceCts;
    private readonly GeneratorRunCache _generatorRunCache = new();
    private readonly RenamePreviewStore _renamePreviews = new();
    private CompilationLru _compilationLru;

    public WorkspaceHost(ISolutionLoader loader, WorkspaceHostOptions options)
    {
        _loader = loader;
        _options = options;
        _compilationLru = new CompilationLru(_options.CompilationLruCapacity);
        if (_options.FileWatcher is not null)
        {
            _watcher = _options.FileWatcher;
            _ownsWatcher = false;
        }
        else
        {
            _watcher = new FileSystemWorkspaceWatcher();
            _ownsWatcher = true;
        }
    }

    public WriteSuppression WriteSuppression => _options.WriteSuppression;

    public RenamePreviewStore RenamePreviews => _renamePreviews;

    public long CurrentEpoch
    {
        get
        {
            lock (_gate)
            {
                return _epoch;
            }
        }
    }

    public WorkspaceStatusDto BeginOpen(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        CancelInFlightUnlocked();
        StopWatcher();
        _renamePreviews.Clear();

        lock (_gate)
        {
            _openedPath = Path.GetFullPath(path);
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
        var openedPath = _openedPath!;
        _loadTask = Task.Run(() => RunLoadAsync(openedPath, cts.Token));

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

            session = new WorkspaceSession(
                _loaded,
                _epoch,
                generatorRunCache: _generatorRunCache,
                compilationLru: _compilationLru);
            return true;
        }
    }

    private void AdvanceEpochUnlocked()
    {
        _epoch++;
        _generatorRunCache.Clear();
        // Replace the instance so in-flight sessions keep the previous epoch's compilations.
        _compilationLru = new CompilationLru(_options.CompilationLruCapacity);
    }

    public StoredRenamePreview StoreRenamePreview(
        string oldHandle,
        string newName,
        IReadOnlyList<RenameDocumentSliceDto> documents,
        IReadOnlyList<string> invalidatedHandles)
    {
        var now = _options.TimeProvider.GetUtcNow();
        return _renamePreviews.Add(
            CurrentEpoch,
            now + _options.RenamePreviewTtl,
            oldHandle,
            newName,
            documents,
            invalidatedHandles);
    }

    public (StoredRenamePreview? Preview, string? ErrorCode) TryGetRenamePreview(string previewId) =>
        _renamePreviews.TryGet(previewId, CurrentEpoch, _options.TimeProvider.GetUtcNow());

    /// <summary>
    /// Drift fallback (ADR-0002): compare disk vs workspace; repair source mismatches; bump epoch when repaired.
    /// Disk I/O runs outside <c>_gate</c>; mutations are serialized under the gate.
    /// </summary>
    public WorkspaceCheckDriftResultDto CheckDrift()
    {
        LoadedSolution loaded;
        string? openedPath;
        lock (_gate)
        {
            if (_phase != "ready" || _loaded is null)
            {
                return new WorkspaceCheckDriftResultDto
                {
                    Epoch = _epoch,
                    Drifted = [],
                    SuggestedAction =
                        "Call workspace_status until phase is ready, then retry workspace_check_drift."
                };
            }

            loaded = _loaded;
            openedPath = _openedPath;
        }

        var extras = loaded.TrackedProjectFilePaths
            .Concat(openedPath is null ? [] : [openedPath])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var detected = loaded.DetectDrift(extras);

        var repairTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drift in detected)
        {
            if (drift.Kind == "ContentMismatch"
                && LoadedSolution.IsSourceFile(drift.Path)
                && File.Exists(drift.Path))
            {
                repairTexts[Path.GetFullPath(drift.Path)] = File.ReadAllText(drift.Path);
            }
        }

        IReadOnlyList<DocumentDrift> drifts;
        long epoch;
        lock (_gate)
        {
            if (!ReferenceEquals(_loaded, loaded) || _phase != "ready")
            {
                return new WorkspaceCheckDriftResultDto
                {
                    Epoch = _epoch,
                    Drifted = detected.Select(ToDto).ToArray(),
                    SuggestedAction =
                        "Workspace changed during drift check; call workspace_check_drift again."
                };
            }

            drifts = loaded.RepairSourceDrifts(detected, repairTexts);
            if (drifts.Any(static d => d.Repaired))
            {
                AdvanceEpochUnlocked();
            }

            epoch = _epoch;
        }

        var projectDrift = drifts.Any(d =>
            !d.Repaired && (d.Kind is "ProjectFileChanged" || LoadedSolution.IsProjectOrSolutionFile(d.Path)));

        var suggested = drifts.Count == 0
            ? "Workspace matches disk for tracked documents."
            : drifts.Any(static d => d.Repaired) && !projectDrift
                ? "Source drifts were repaired and the workspace epoch advanced; retry queries without stale cursors."
                : projectDrift
                    ? "Project or solution files drifted; call workspace_open on the same path to fully reload."
                    : "Inspect drifted paths; source mismatches that could not be repaired may need workspace_open.";

        return new WorkspaceCheckDriftResultDto
        {
            Epoch = epoch,
            Drifted = drifts.Select(ToDto).ToArray(),
            SuggestedAction = suggested
        };
    }

    /// <summary>
    /// Applies a batch of changed paths (debounce flush / tests). Disk reads happen outside the gate.
    /// </summary>
    public void ApplyChangedPaths(IEnumerable<string> paths)
    {
        var filtered = paths
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetFullPath)
            .Where(p => LoadedSolution.IsWatchedFile(p))
            .Where(p => !_options.WriteSuppression.IsSuppressed(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (filtered.Length == 0)
        {
            return;
        }

        var needsReload = filtered.Any(LoadedSolution.IsProjectOrSolutionFile);
        if (needsReload)
        {
            string? reopen;
            lock (_gate)
            {
                reopen = _openedPath;
            }

            if (reopen is not null)
            {
                BeginOpen(reopen);
            }

            return;
        }

        var diskTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in filtered)
        {
            if (File.Exists(path))
            {
                diskTexts[path] = File.ReadAllText(path);
            }
        }

        lock (_gate)
        {
            if (_phase != "ready" || _loaded is null)
            {
                return;
            }

            var changed = false;
            foreach (var (path, text) in diskTexts)
            {
                if (_loaded.TryUpdateDocumentFromText(path, SourceText.From(text)))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                AdvanceEpochUnlocked();
            }
        }
    }

    private static DriftItemDto ToDto(DocumentDrift d) => new()
    {
        Path = d.Path,
        Kind = d.Kind,
        Repaired = d.Repaired
    };

    private void OnWatcherPathsChanged(IReadOnlyList<string> paths)
    {
        string[]? syncBatch = null;

        lock (_debounceGate)
        {
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (!LoadedSolution.IsWatchedFile(path))
                {
                    continue;
                }

                if (_options.WriteSuppression.IsSuppressed(path))
                {
                    continue;
                }

                _pendingPaths.Add(Path.GetFullPath(path));
            }

            if (_pendingPaths.Count == 0)
            {
                return;
            }

            // Zero debounce: apply synchronously for deterministic tests.
            if (_options.Debounce <= TimeSpan.Zero)
            {
                syncBatch = _pendingPaths.ToArray();
                _pendingPaths.Clear();
            }
            else
            {
                _debounceCts?.Cancel();
                _debounceCts?.Dispose();
                _debounceCts = new CancellationTokenSource();
                var token = _debounceCts.Token;
                var delay = _options.Debounce;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(delay, token).ConfigureAwait(false);

                        string[] batch;
                        lock (_debounceGate)
                        {
                            batch = _pendingPaths.ToArray();
                            _pendingPaths.Clear();
                        }

                        if (batch.Length > 0)
                        {
                            ApplyChangedPaths(batch);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // superseded by a newer debounce window
                    }
                }, CancellationToken.None);
            }
        }

        if (syncBatch is { Length: > 0 })
        {
            ApplyChangedPaths(syncBatch);
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
                AdvanceEpochUnlocked();
                _phase = "ready";
                _elapsed.Stop();
                _estimatedRemainingMs = 0;
                _error = null;
                if (_openedPath is not null)
                {
                    loaded.RecordProjectFileSnapshots([_openedPath]);
                }
            }

            StartWatcherForLoaded(loaded);
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

    private void StartWatcherForLoaded(LoadedSolution loaded)
    {
        var roots = loaded.TrackedDocumentPaths
            .Select(static p => Path.GetDirectoryName(p))
            .Where(static d => !string.IsNullOrWhiteSpace(d))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .ToArray();

        // Also watch the opened solution's directory.
        lock (_gate)
        {
            if (_openedPath is not null)
            {
                var openDir = Path.GetDirectoryName(_openedPath);
                if (!string.IsNullOrWhiteSpace(openDir) && Directory.Exists(openDir))
                {
                    roots = roots
                        .Append(openDir)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
            }
        }

        if (roots.Length == 0)
        {
            return;
        }

        try
        {
            _watcher.Start(roots, OnWatcherPathsChanged);
        }
        catch
        {
            // Watcher failures must not take down the ready workspace; check-drift remains as fallback.
        }
    }

    private void StopWatcher()
    {
        try
        {
            _watcher.Stop();
        }
        catch
        {
            // ignore
        }

        lock (_debounceGate)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
            _pendingPaths.Clear();
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
        StopWatcher();

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

        if (_ownsWatcher)
        {
            _watcher.Dispose();
        }

        _loadMutex.Dispose();
    }
}
