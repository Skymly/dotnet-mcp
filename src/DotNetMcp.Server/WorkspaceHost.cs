using System.Diagnostics;
using System.Text;
using DotNetMcp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Server;

/// <summary>
/// Single active workspace coordinator (ADR-0003 §1/§4): open returns immediately; status polls.
/// Freshness via internal FSW + debounce + epoch (ADR-0002 §3).
/// </summary>
public sealed class WorkspaceHost : IWorkspaceEditWriter, IAsyncDisposable
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

    private readonly HashSet<string> _pendingPaths = new(PathPolicy.Comparer);
    private CancellationTokenSource? _debounceCts;
    private readonly GeneratorRunCache _generatorRunCache = new();
    private readonly FindHitCache _findHitCache = new();
    private CancellationTokenSource? _warmCts;
    private long _generation;
    private CompilationLru _compilationLru;
    private readonly TrustedRoots _trustedRoots;
    private readonly GeneratorQueryService? _generators;
    private FSharpWorkspaceSnapshot? _fsharpSnapshot;
    private volatile bool _disposed;

    public WorkspaceHost(
        ISolutionLoader loader,
        WorkspaceHostOptions options,
        TrustedRoots trustedRoots,
        GeneratorQueryService? generators = null)
    {
        _loader = loader;
        _options = options;
        _trustedRoots = trustedRoots ?? throw new ArgumentNullException(nameof(trustedRoots));
        _generators = generators;
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

    public long Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    public bool PathExists(string path) => File.Exists(path);

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
        ObjectDisposedException.ThrowIf(_disposed, this);

        StopWatcher();

        CancellationTokenSource? oldCts;
        CancellationTokenSource cts;
        string openedPath;
        long generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancelWarmUnlocked();
            oldCts = _loadCts;
            _generation++;
            generation = _generation;
            _openedPath = Path.GetFullPath(path);
            _phase = "loading";
            _completedUnits = 0;
            _totalUnits = 1;
            _error = null;
            _warnings = [];
            _estimatedRemainingMs = 0;
            _elapsed.Restart();
            cts = new CancellationTokenSource();
            _loadCts = cts;
            openedPath = _openedPath;
            // Do not await a previous load here: StartWatcher can raise FileSystemWatcher
            // events on this thread (Windows). Awaiting that in-flight task deadlocks.
            // Loads are already serialized by _loadMutex; DisposeAsync re-joins _loadTask.
            _loadTask = Task.Run(() => RunLoadAsync(openedPath, cts, generation));
        }

        try
        {
            oldCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

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
                fsharpSnapshot: _fsharpSnapshot,
                generatorRunCache: _generatorRunCache,
                compilationLru: _compilationLru,
                findHitCache: _findHitCache);
            return true;
        }
    }

    private void AdvanceEpochUnlocked()
    {
        _epoch++;
        _generatorRunCache.Clear();
        _findHitCache.Clear();
        _generators?.DiscardListCacheExceptEpoch(_epoch);
        CancelWarmUnlocked();
        // Replace the instance so in-flight sessions keep the previous epoch's compilations.
        _compilationLru = new CompilationLru(_options.CompilationLruCapacity);
        // Empty snapshot at the new epoch until CaptureFSharpOutsideGate commits I/O.
        // Never leave a previous epoch's F# texts on a newer session.
        _fsharpSnapshot = new FSharpWorkspaceSnapshot(_epoch, []);
        // F# snapshot capture does disk I/O / GetResult — run outside _gate.
    }

    private void CaptureFSharpOutsideGate()
    {
        LoadedSolution? loaded;
        long epoch;
        lock (_gate)
        {
            loaded = _loaded;
            epoch = _epoch;
        }

        var snap = loaded is null
            ? null
            : WorkspaceSession.CaptureFSharpSnapshot(loaded.Solution, epoch, _trustedRoots);

        lock (_gate)
        {
            if (_epoch == epoch)
            {
                _fsharpSnapshot = snap;
            }
        }
    }

    public WorkspaceEditOutcome<long> WriteDeclaredPaths(IReadOnlyList<WorkspaceEditDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        LoadedSolution loaded;
        long epochAtStart;
        FSharpWorkspaceSnapshot? fsharp;
        lock (_gate)
        {
            if (_phase != "ready" || _loaded is null)
            {
                return FailWrite(
                    PolicyErrorCodes.WorkspaceNotReady,
                    "Workspace is not ready; apply did not write.",
                    "Call workspace_status until ready, then preview and apply again.");
            }

            loaded = _loaded;
            epochAtStart = _epoch;
            fsharp = _fsharpSnapshot;
        }

        var prepared = new List<(WorkspaceEditDocument Document, string FinalPath, Encoding Encoding)>(documents.Count);
        foreach (var document in documents)
        {
            if (!_trustedRoots.Contains(document.Path))
            {
                return FailWrite(
                    PolicyErrorCodes.PathOutsideTrustedRoots,
                    "A preview document is outside trusted roots; nothing was written.",
                    "Re-open the workspace under a trusted root that contains every preview path.");
            }

            if (!TryReadSnapshotText(loaded, fsharp, document.Path, out var snapshotText)
                || !File.Exists(document.Path))
            {
                return FailWrite(
                    PolicyErrorCodes.PreviewTargetMissing,
                    "A preview document is not in the ready workspace; nothing was written.",
                    "Call the matching preview tool again on the current snapshot.");
            }

            string diskText;
            Encoding encoding;
            try
            {
                (diskText, encoding) = FileTextCodec.Read(document.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return FailWrite(
                    PolicyErrorCodes.WorkspaceEditApplyFailed,
                    "A preview document could not be read: " + ex.Message,
                    "Retry apply with the same previewId, or preview again if the disk changed.");
            }

            if (!string.Equals(snapshotText, document.OldText, StringComparison.Ordinal)
                || !string.Equals(diskText, document.OldText, StringComparison.Ordinal))
            {
                return FailWrite(
                    PolicyErrorCodes.PreviewTextMismatch,
                    "A preview document no longer matches OldText; nothing was written.",
                    "Call the matching preview tool again on the current snapshot.");
            }

            string finalPath;
            try
            {
                finalPath = PathPolicy.Normalize(document.Path);
            }
            catch (Exception ex) when (ex is PathPolicyException or ArgumentException)
            {
                return FailWrite(
                    PolicyErrorCodes.PathOutsideTrustedRoots,
                    "A preview document path could not be canonicalized; nothing was written.",
                    "Call the matching preview tool again on the current snapshot under a trusted root.");
            }

            if (!_trustedRoots.ContainsNormalized(finalPath))
            {
                return FailWrite(
                    PolicyErrorCodes.PathOutsideTrustedRoots,
                    "A preview document resolves outside trusted roots; nothing was written.",
                    "Re-open the workspace under a trusted root that contains every preview path.");
            }

            prepared.Add((document, finalPath, encoding));
        }

        var paths = prepared.Select(static r => r.FinalPath).ToArray();
        using (_options.WriteSuppression.Suppress(paths))
        {
            var writtenCount = 0;
            try
            {
                foreach (var (document, finalPath, encoding) in prepared)
                {
                    FileTextCodec.Write(finalPath, document.NewText, encoding);
                    writtenCount++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RollbackDeclaredPaths(loaded, prepared, writtenCount, includeCurrent: true);
                return FailWrite(
                    PolicyErrorCodes.WorkspaceEditApplyFailed,
                    "Apply failed while writing preview documents: " + ex.Message,
                    "Retry apply with the same previewId; disk was rolled back to OldText.");
            }

            lock (_gate)
            {
                if (_phase != "ready" || !ReferenceEquals(_loaded, loaded) || _epoch != epochAtStart)
                {
                    RollbackDeclaredPaths(loaded, prepared, writtenCount, includeCurrent: false);
                    return FailWrite(
                        PolicyErrorCodes.PreviewEpochMismatch,
                        "Workspace epoch changed before apply could commit; disk was rolled back.",
                        "Call the matching preview tool again on the current snapshot.");
                }

                foreach (var (document, _, _) in prepared)
                {
                    if (!loaded.TryUpdateDocumentFromText(
                            document.Path,
                            SourceText.From(document.NewText))
                        && !TryReadFSharpSnapshotText(fsharp, document.Path, out _))
                    {
                        RollbackDeclaredPaths(loaded, prepared, writtenCount, includeCurrent: false);
                        return FailWrite(
                            PolicyErrorCodes.PreviewTargetMissing,
                            "A preview document is not in the ready workspace; nothing was written.",
                            "Call the matching preview tool again on the current snapshot.");
                    }
                }

                AdvanceEpochUnlocked();
            }
        }

        CaptureFSharpOutsideGate();
        lock (_gate)
        {
            return new WorkspaceEditOutcome<long>(_epoch, null);
        }
    }

    private static WorkspaceEditOutcome<long> FailWrite(string error, string message, string suggested) =>
        new(
            0,
            new PolicyErrorDto
            {
                Error = error,
                Message = message,
                SuggestedAction = suggested
            });

    private static bool TryReadSnapshotText(
        LoadedSolution loaded,
        FSharpWorkspaceSnapshot? fsharp,
        string path,
        out string text)
    {
        if (loaded.TryGetDocumentId(path, out var documentId))
        {
            var document = loaded.Solution.GetDocument(documentId);
            if (document is not null)
            {
                text = document.GetTextAsync(CancellationToken.None).GetAwaiter().GetResult().ToString();
                return true;
            }
        }

        return TryReadFSharpSnapshotText(fsharp, path, out text);
    }

    private static bool TryReadFSharpSnapshotText(FSharpWorkspaceSnapshot? fsharp, string path, out string text)
    {
        text = string.Empty;
        if (fsharp is null || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var project in fsharp.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (SameSnapshotPath(document.Path, path))
                {
                    text = document.Text;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool SameSnapshotPath(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            return string.Equals(
                PathPolicy.Normalize(left),
                PathPolicy.Normalize(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is PathPolicyException or ArgumentException)
        {
            return false;
        }
    }

    private static void RollbackDeclaredPaths(
        LoadedSolution loaded,
        IReadOnlyList<(WorkspaceEditDocument Document, string FinalPath, Encoding Encoding)> writes,
        int writtenCount,
        bool includeCurrent)
    {
        var end = Math.Min(writes.Count, includeCurrent ? writtenCount + 1 : writtenCount);
        for (var i = 0; i < end; i++)
        {
            try
            {
                FileTextCodec.Write(writes[i].FinalPath, writes[i].Document.OldText, writes[i].Encoding);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep the original apply failure; do not replace it with rollback I/O.
            }
        }

        foreach (var (document, _, _) in writes)
        {
            loaded.TryUpdateDocumentFromText(document.Path, SourceText.From(document.OldText));
        }
    }

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
            .Distinct(PathPolicy.Comparer)
            .ToArray();

        var detected = loaded.DetectDrift(extras, _trustedRoots.Contains);

        var repairTexts = new Dictionary<string, string>(PathPolicy.Comparer);
        foreach (var drift in detected)
        {
            if (drift.Kind == "ContentMismatch"
                && LoadedSolution.IsSourceFile(drift.Path)
                && LoadedSolution.TryReadTrustedDiskText(drift.Path, _trustedRoots, out var diskText))
            {
                var key = TryNormalize(drift.Path);
                if (key is not null)
                {
                    repairTexts[key] = diskText;
                }
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

        CaptureFSharpOutsideGate();

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
        if (_disposed)
        {
            return;
        }

        var filtered = paths
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Where(_trustedRoots.Contains)
            .Select(TryNormalize)
            .Where(static p => p is not null)
            .Cast<string>()
            .Where(p => LoadedSolution.IsWatchedFile(p))
            .Where(p => !_options.WriteSuppression.IsSuppressed(p))
            .Distinct(PathPolicy.Comparer)
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

            if (reopen is not null && !_disposed)
            {
                try
                {
                    BeginOpen(reopen);
                }
                catch (ObjectDisposedException)
                {
                }
            }

            return;
        }

        var diskTexts = new Dictionary<string, string>(PathPolicy.Comparer);
        foreach (var path in filtered)
        {
            if (LoadedSolution.TryReadTrustedDiskText(path, _trustedRoots, out var diskText))
            {
                diskTexts[path] = diskText;
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
                else if (LoadedSolution.IsSourceFile(path) &&
                         (path.EndsWith(".fs", StringComparison.OrdinalIgnoreCase) ||
                          path.EndsWith(".fsi", StringComparison.OrdinalIgnoreCase)))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                AdvanceEpochUnlocked();
            }
        }

        CaptureFSharpOutsideGate();
    }

    private static DriftItemDto ToDto(DocumentDrift d) => new()
    {
        Path = d.Path,
        Kind = d.Kind,
        Repaired = d.Repaired
    };

    private void OnWatcherPathsChanged(IReadOnlyList<string> paths)
    {
        if (_disposed)
        {
            return;
        }

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

                var normalized = TryNormalize(path);
                if (normalized is null || !_trustedRoots.ContainsNormalized(normalized))
                {
                    continue;
                }

                _pendingPaths.Add(normalized);
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

    private async Task RunLoadAsync(string path, CancellationTokenSource cts, long generation)
    {
        var ct = cts.Token;
        var acquired = false;
        try
        {
            await _loadMutex.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            ct.ThrowIfCancellationRequested();

            // Dispose previous loaded workspace after acquiring the mutex.
            LoadedSolution? previous;
            lock (_gate)
            {
                if (generation != _generation)
                {
                    return;
                }

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
                    if (_phase is not "loading" || generation != _generation)
                    {
                        return;
                    }

                    _completedUnits = p.CompletedUnits;
                    _totalUnits = Math.Max(1, p.TotalUnits);
                    UpdateEstimateUnlocked();
                }
            });

            var loaded = await _loader.OpenAsync(path, progress, ct).ConfigureAwait(false);

            try
            {
                TrustedGraphGate.EnsureLoadedSolutionUnderRoots(loaded, _trustedRoots);
            }
            catch
            {
                await loaded.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            var committed = false;
            lock (_gate)
            {
                if (ct.IsCancellationRequested || generation != _generation)
                {
                    if (generation == _generation)
                    {
                        _phase = "cancelled";
                        _elapsed.Stop();
                        _estimatedRemainingMs = 0;
                    }
                }
                else
                {
                    _loaded = loaded;
                    _warnings = loaded.Warnings;
                    _completedUnits = Math.Max(_completedUnits, loaded.Solution.ProjectIds.Count);
                    _totalUnits = Math.Max(1, loaded.Solution.ProjectIds.Count);
                    AdvanceEpochUnlocked();
                    _elapsed.Stop();
                    _estimatedRemainingMs = 0;
                    _error = null;
                    if (_openedPath is not null)
                    {
                        loaded.RecordProjectFileSnapshots([_openedPath]);
                    }

                    committed = true;
                }
            }

            if (!committed)
            {
                await loaded.DisposeAsync().ConfigureAwait(false);
                return;
            }

            lock (_gate)
            {
                if (generation != _generation || !ReferenceEquals(_loaded, loaded))
                {
                    return;
                }
            }

            CaptureFSharpOutsideGate();
            StartWatcherForLoaded(loaded);
            lock (_gate)
            {
                if (generation == _generation && ReferenceEquals(_loaded, loaded) && _phase != "failed" && _phase != "cancelled")
                {
                    _phase = "ready";
                }
            }

            StartBackgroundWarm(loaded, path);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _phase = "cancelled";
                    _elapsed.Stop();
                    _estimatedRemainingMs = 0;
                }
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _phase = "failed";
                    _error = ex.Message;
                    _elapsed.Stop();
                    _estimatedRemainingMs = 0;
                }
            }
        }
        finally
        {
            if (acquired)
            {
                try
                {
                    _loadMutex.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            lock (_gate)
            {
                if (ReferenceEquals(_loadCts, cts))
                {
                    _loadCts = null;
                }
            }

            cts.Dispose();
        }
    }

    private void StartWatcherForLoaded(LoadedSolution loaded)
    {
        var roots = loaded.TrackedDocumentPaths
            .Select(static p => Path.GetDirectoryName(p))
            .Where(static d => !string.IsNullOrWhiteSpace(d))
            .Cast<string>()
            .Distinct(PathPolicy.Comparer)
            .Where(d => Directory.Exists(d) && _trustedRoots.Contains(d))
            .ToArray();

        // Also watch the opened solution's directory.
        lock (_gate)
        {
            if (_openedPath is not null)
            {
                var openDir = Path.GetDirectoryName(_openedPath);
                if (!string.IsNullOrWhiteSpace(openDir) && Directory.Exists(openDir))
                {
                    if (_trustedRoots.Contains(openDir))
                    {
                        roots = roots
                            .Append(openDir)
                            .Distinct(PathPolicy.Comparer)
                            .ToArray();
                    }
                }
            }
        }

        if (roots.Length == 0)
        {
            return;
        }

        try
        {
            _watcher.Start(roots, OnWatcherPathsChanged, OnWatchLost);
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
            CancelWarmUnlocked();
        }

        try
        {
            // Cancel only. RunLoadAsync disposes the CTS after the in-flight load releases Token.
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


    private void CancelWarmUnlocked()
    {
        try
        {
            _warmCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void StartBackgroundWarm(LoadedSolution loaded, string openedPath)
    {
        CancellationTokenSource cts;
        CompilationLru lru;
        long epoch;
        FSharpWorkspaceSnapshot? fsharpSnapshot;
        lock (_gate)
        {
            try
            {
                _warmCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            cts = new CancellationTokenSource();
            _warmCts = cts;
            lru = _compilationLru;
            epoch = _epoch;
            fsharpSnapshot = _fsharpSnapshot;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await WarmCompilationsAsync(loaded, openedPath, lru, epoch, fsharpSnapshot, cts.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_warmCts, cts))
                    {
                        _warmCts = null;
                    }
                }

                cts.Dispose();
            }
        });
    }

    private async Task WarmCompilationsAsync(
        LoadedSolution loaded,
        string openedPath,
        CompilationLru lru,
        long epoch,
        FSharpWorkspaceSnapshot? fsharpSnapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            using var session = new WorkspaceSession(
                loaded,
                epoch,
                fsharpSnapshot: fsharpSnapshot,
                compilationLru: lru,
                generatorRunCache: _generatorRunCache,
                findHitCache: _findHitCache);
            foreach (var project in SelectWarmProjects(loaded.Solution, openedPath, lru.Capacity ?? 50))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await session.GetCompilationAsync(project.Id, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Warm is best-effort; never flip phase to failed.
        }
    }

    internal static IReadOnlyList<Project> SelectWarmProjects(Solution solution, string openedPath, int cap)
    {
        cap = Math.Max(1, cap);
        var ext = Path.GetExtension(openedPath);
        var isProject = ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".fsproj", StringComparison.OrdinalIgnoreCase);

        if (isProject)
        {
            var seed = solution.Projects.FirstOrDefault(p =>
                p.FilePath is not null &&
                string.Equals(Path.GetFullPath(p.FilePath), Path.GetFullPath(openedPath), StringComparison.OrdinalIgnoreCase));
            if (seed is null)
            {
                return [];
            }

            return FindRefsScopes.ProjectsInClosure(solution, seed).Take(cap).ToArray();
        }

        return solution.Projects
            .OrderBy(p => WarmPreference(p.Name))
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(cap)
            .ToArray();
    }

    private static int WarmPreference(string name)
    {
        if (name.Contains("Test", StringComparison.OrdinalIgnoreCase)) return 100;
        if (name.Contains("Bench", StringComparison.OrdinalIgnoreCase)) return 90;
        if (name.Contains("Dummy", StringComparison.OrdinalIgnoreCase)) return 80;
        return 0;
    }


    private void OnWatchLost()
    {
        try
        {
            CheckDrift();
        }
        catch
        {
            // Drift fallback must not throw out of the watcher thread.
        }
    }

    private static string? TryNormalize(string path)
    {
        try
        {
            return PathPolicy.Normalize(path);
        }
        catch (Exception ex) when (ex is PathPolicyException or ArgumentException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? loadTask;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation++;
            loadTask = _loadTask;
        }

        CancelInFlightUnlocked();
        StopWatcher();

        while (loadTask is not null)
        {
            try
            {
                await loadTask.ConfigureAwait(false);
            }
            catch
            {
                // ignored during shutdown
            }

            lock (_gate)
            {
                if (ReferenceEquals(_loadTask, loadTask))
                {
                    break;
                }

                loadTask = _loadTask;
            }
        }

        LoadedSolution? loaded;
        lock (_gate)
        {
            loaded = _loaded;
            _loaded = null;
            _loadTask = null;
            _loadCts = null;
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


