namespace DotNetMcp.Server;

/// <summary>
/// Mechanical write seam for Workspace Edit (ADR-0005). Host does not see previewId.
/// Production adapter: WorkspaceHost. Test adapter: in-memory, no disk.
/// </summary>
public interface IWorkspaceEditWriter
{
    long CurrentEpoch { get; }

    /// <summary>Increments on BeginOpen. Workspace Edit drops stored previews when this changes.</summary>
    long Generation { get; }

    bool PathExists(string path);

    /// <summary>WriteSuppression → write declared paths → backfill → Epoch++. No previewId, kind, or TTL.</summary>
    WorkspaceEditOutcome<long> WriteDeclaredPaths(IReadOnlyList<WorkspaceEditDocument> documents);
}
