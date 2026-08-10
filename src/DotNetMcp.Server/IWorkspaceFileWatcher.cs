namespace DotNetMcp.Server;

/// <summary>
/// Abstraction over FileSystemWatcher so tests can inject deterministic change notifications.
/// </summary>
public interface IWorkspaceFileWatcher : IDisposable
{
    /// <summary>
    /// Begin watching <paramref name="roots"/> (directories). Invokes <paramref name="onPathsChanged"/>
    /// with changed file paths (may be coalesced by the caller).
    /// </summary>
    void Start(IReadOnlyList<string> roots, Action<IReadOnlyList<string>> onPathsChanged);

    void Stop();
}

/// <summary>
/// Production watcher: one <see cref="FileSystemWatcher"/> per root directory.
/// </summary>
public sealed class FileSystemWorkspaceWatcher : IWorkspaceFileWatcher
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private Action<IReadOnlyList<string>>? _onPathsChanged;
    private bool _disposed;

    public void Start(IReadOnlyList<string> roots, Action<IReadOnlyList<string>> onPathsChanged)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        _onPathsChanged = onPathsChanged;

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            var watcher = new FileSystemWatcher(root)
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
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    public void Stop()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnEvent;
            watcher.Created -= OnEvent;
            watcher.Deleted -= OnEvent;
            watcher.Renamed -= OnRenamed;
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _onPathsChanged = null;
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.FullPath))
        {
            return;
        }

        _onPathsChanged?.Invoke([e.FullPath]);
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

        if (paths.Count > 0)
        {
            _onPathsChanged?.Invoke(paths);
        }
    }
}
