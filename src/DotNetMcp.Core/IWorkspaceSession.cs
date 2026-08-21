using Microsoft.CodeAnalysis;

namespace DotNetMcp.Core;

/// <summary>
/// Request-scoped workspace snapshot (ADR-0002): one epoch, frozen Roslyn
/// <see cref="Solution"/> beside an F# source snapshot, and pull-based
/// compilation / generator attribution APIs.
/// </summary>
public interface IWorkspaceSession : IDisposable
{
    long Epoch { get; }

    Solution Solution { get; }

    /// <summary>
    /// F# project/source snapshot frozen with <see cref="Epoch"/>.
    /// FCS adapter uses this, not <see cref="Solution"/>.
    /// </summary>
    FSharpWorkspaceSnapshot FSharpSnapshot { get; }

    /// <summary>Lazy compilation including workspace-run generated trees.</summary>
    Task<Compilation> GetCompilationAsync(ProjectId projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compilation with workspace-generated trees stripped — for self-built GeneratorDriver
    /// attribution (ADR-0001 §6).
    /// </summary>
    Task<Compilation> GetCompilationWithoutGeneratedTreesAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Per-generator attribution driver result, cached by (projectId, epoch).
    /// </summary>
    Task<DriverRunSnapshot> GetGeneratorRunResultAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default);
}
