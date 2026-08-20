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

    /// <summary>
    /// Per-request compilation LRU capacity (ADR-0002 / Spike S2). Default 50; &lt;= 0 is unlimited.
    /// </summary>
    public int CompilationLruCapacity { get; init; } = WorkspaceSession.DefaultCompilationLruCapacity;

    /// <summary>TTL for stored Workspace Edit previews (ADR-0005). Default 5 minutes.</summary>
    public TimeSpan WorkspaceEditPreviewTtl { get; init; } = TimeSpan.FromMinutes(5);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
