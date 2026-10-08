namespace DotNetMcp.Server;

/// <summary>
/// Abstraction over FileSystemWatcher so tests can inject deterministic change notifications.
/// </summary>
public interface IWorkspaceFileWatcher : IDisposable
{
    /// <summary>
    /// Begin watching <paramref name="roots"/> (directories). Invokes <paramref name="onPathsChanged"/>
    /// with changed file paths (may be coalesced by the caller).
    /// <paramref name="onWatchLost"/> is raised when the underlying watcher reports an error
    /// (including internal buffer overflow) so the host can fall back to drift scan.
    /// </summary>
    void Start(
        IReadOnlyList<string> roots,
        Action<IReadOnlyList<string>> onPathsChanged,
        Action? onWatchLost = null);

    void Stop();
}

/// <summary>
/// Production watcher: one <see cref="FileSystemWatcher"/> per root directory.
/// </summary>
public sealed class FileSystemWorkspaceWatcher : IWorkspaceFileWatcher
{
    private readonly object _sync = new();
    private readonly List<ErrorRaisableWatcher> _watchers = [];
    private Action<IReadOnlyList<string>>? _onPathsChanged;
    private Action? _onWatchLost;
    private bool _disposed;

    internal object SyncForTests => _sync;

    /// <summary>Test seam. Must not call back into this watcher.</summary>
    internal Action? BeforeAddingWatcherForTests { get; set; }

    public void Start(
        IReadOnlyList<string> roots,
        Action<IReadOnlyList<string>> onPathsChanged,
        Action? onWatchLost = null)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopCore();
            _onPathsChanged = onPathsChanged;
            _onWatchLost = onWatchLost;

            foreach (var root in roots.Distinct(PathPolicy.Comparer))
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    continue;
                }

                var watcher = new ErrorRaisableWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName
                                   | NotifyFilters.DirectoryName
                                   | NotifyFilters.LastWrite
                                   | NotifyFilters.Size
                                   | NotifyFilters.CreationTime,
                    Filter = "*.*"
                };

                watcher.Changed += OnEvent;
                watcher.Created += OnEvent;
                watcher.Deleted += OnEvent;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;
                BeforeAddingWatcherForTests?.Invoke();
                _watchers.Add(watcher);
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            StopCore();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopCore();
        }
    }

    private void StopCore()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnEvent;
            watcher.Created -= OnEvent;
            watcher.Deleted -= OnEvent;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
        }

        _watchers.Clear();
        _onPathsChanged = null;
        _onWatchLost = null;
    }

    /// <summary>
    /// Raises <see cref="FileSystemWatcher.Error"/> on each active watcher so tests can
    /// prove the production subscription still calls watch-lost. Production never calls this.
    /// </summary>
    internal void RaiseErrorForTests()
    {
        ErrorRaisableWatcher[] snapshot;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_watchers.Count == 0)
            {
                throw new InvalidOperationException("FileSystemWorkspaceWatcher has no active watchers.");
            }

            snapshot = _watchers.ToArray();
        }

        var args = new ErrorEventArgs(new InternalBufferOverflowException("injected watch error"));
        foreach (var watcher in snapshot)
        {
            watcher.RaiseError(args);
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.FullPath))
        {
            return;
        }

        Action<IReadOnlyList<string>>? callback;
        lock (_sync)
        {
            callback = _onPathsChanged;
        }

        callback?.Invoke([e.FullPath]);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        var paths = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(e.OldFullPath))
        {
            paths.Add(e.OldFullPath);
        }

        if (!string.IsNullOrWhiteSpace(e.FullPath))
        {
            paths.Add(e.FullPath);
        }

        if (paths.Count == 0)
        {
            return;
        }

        Action<IReadOnlyList<string>>? callback;
        lock (_sync)
        {
            callback = _onPathsChanged;
        }

        callback?.Invoke(paths);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        Action? callback;
        lock (_sync)
        {
            callback = _onWatchLost;
        }

        callback?.Invoke();
    }

    /// <summary>
    /// Subclass only so tests can raise the protected error event.
    /// Production behavior is otherwise <see cref="FileSystemWatcher"/>.
    /// </summary>
    private sealed class ErrorRaisableWatcher : FileSystemWatcher
    {
        public ErrorRaisableWatcher(string path)
            : base(path)
        {
        }

        public void RaiseError(ErrorEventArgs args) => OnError(args);
    }
}
