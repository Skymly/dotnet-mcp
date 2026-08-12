using Microsoft.CodeAnalysis;

namespace DotNetMcp.Core;

/// <summary>
/// Request-scoped workspace snapshot (ADR-0002): one epoch, frozen <see cref="Solution"/>,
/// and pull-based compilation / generator attribution APIs.
/// </summary>
public interface IWorkspaceSession : IDisposable
{
    long Epoch { get; }

    Solution Solution { get; }

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
