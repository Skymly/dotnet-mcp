using Microsoft.CodeAnalysis;

namespace DotNetMcp.Server;

/// <summary>
/// Minimal request-scoped workspace snapshot (ADR-0002). Compilation APIs arrive in later tickets.
/// </summary>
public interface IWorkspaceSession : IDisposable
{
    long Epoch { get; }
    Solution Solution { get; }
    IReadOnlyList<ProjectSummaryDto> ListProjects();
}

public sealed class WorkspaceSession : IWorkspaceSession
{
    private bool _disposed;

    public WorkspaceSession(LoadedSolution loaded, long epoch)
    {
        // Freeze snapshot at request start so FSW updates cannot cross a mid-request boundary (ADR-0002).
        Solution = loaded.Solution;
        Epoch = epoch;
    }

    public long Epoch { get; }
    public Solution Solution { get; }

    public IReadOnlyList<ProjectSummaryDto> ListProjects() =>
        ProjectSummary.FromSolution(Solution);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // LoadedSolution lifetime is owned by WorkspaceHost, not per-request Dispose.
    }
}
