using DotNetMcp.Server;

namespace DotNetMcp.Tests;

/// <summary>
/// Test double: raise path changes manually (injected via <see cref="WorkspaceHostOptions.FileWatcher"/>).
/// </summary>
public sealed class ManualWorkspaceFileWatcher : IWorkspaceFileWatcher
{
    private Action<IReadOnlyList<string>>? _onPathsChanged;
    private bool _disposed;

    public bool IsStarted { get; private set; }

    public void Start(IReadOnlyList<string> roots, Action<IReadOnlyList<string>> onPathsChanged)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _onPathsChanged = onPathsChanged;
        IsStarted = true;
    }

    public void Stop()
    {
        IsStarted = false;
        _onPathsChanged = null;
    }

    public void Raise(params string[] paths)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_onPathsChanged is null || paths.Length == 0)
        {
            return;
        }

        _onPathsChanged(paths);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
