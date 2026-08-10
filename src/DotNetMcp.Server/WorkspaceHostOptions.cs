namespace DotNetMcp.Server;

/// <summary>
/// Tunables / seams for workspace freshness (ADR-0002).
/// </summary>
public sealed class WorkspaceHostOptions
{
    public static WorkspaceHostOptions Default { get; } = new();

    /// <summary>Debounce window for coalescing FSW events. Use <see cref="TimeSpan.Zero"/> in tests.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Optional watcher. When null, <see cref="WorkspaceHost"/> creates a <see cref="FileSystemWorkspaceWatcher"/>.
    /// </summary>
    public IWorkspaceFileWatcher? FileWatcher { get; init; }

    public WriteSuppression WriteSuppression { get; init; } = new();
}
