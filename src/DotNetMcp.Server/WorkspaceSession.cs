using DotNetMcp.Core;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Server;

/// <summary>
/// Request-scoped workspace snapshot implementing ADR-0002 compilation / generator APIs.
/// </summary>
public sealed class WorkspaceSession : IWorkspaceSession
{
    public const int DefaultCompilationLruCapacity = 50;

    private readonly CompilationLru _compilationLru;
    private readonly GeneratorRunCache _generatorRunCache;
    private bool _disposed;

    public WorkspaceSession(
        LoadedSolution loaded,
        long epoch,
        int compilationLruCapacity = DefaultCompilationLruCapacity,
        GeneratorRunCache? generatorRunCache = null)
    {
        // Freeze snapshot at request start so FSW updates cannot cross a mid-request boundary (ADR-0002).
        Solution = loaded.Solution;
        Epoch = epoch;
        _compilationLru = new CompilationLru(compilationLruCapacity);
        _generatorRunCache = generatorRunCache ?? new GeneratorRunCache();
    }

    public long Epoch { get; }

    public Solution Solution { get; }

    /// <summary>Test/observability hook for the per-session compilation LRU.</summary>
    public CompilationLru CompilationCache => _compilationLru;

    public IReadOnlyList<ProjectSummaryDto> ListProjects() =>
        ProjectSummary.FromSolution(Solution);

    public async Task<Compilation> GetCompilationAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var project = RequireProject(projectId);
        return await _compilationLru
            .GetOrAddAsync(project, CompileAsync, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Compilation> GetCompilationWithoutGeneratedTreesAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var project = RequireProject(projectId);
        var full = await GetCompilationAsync(projectId, cancellationToken).ConfigureAwait(false);
        return await GeneratorDriverRunner
            .StripGeneratedTreesFromProjectAsync(project, full, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DriverRunSnapshot> GetGeneratorRunResultAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var project = RequireProject(projectId);
        var key = project.Id.Id.ToString("D");

        if (_generatorRunCache.TryGet(key, Epoch, out var cached))
        {
            return cached;
        }

        var baseCompilation = await GetCompilationWithoutGeneratedTreesAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        var snapshot = GeneratorDriverRunner.RunDriver(project, baseCompilation, cancellationToken);
        _generatorRunCache.Set(key, Epoch, snapshot);
        return snapshot;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // LoadedSolution lifetime is owned by WorkspaceHost, not per-request Dispose.
    }

    private Project RequireProject(ProjectId projectId)
    {
        var project = Solution.GetProject(projectId)
            ?? throw new InvalidOperationException($"Project '{projectId.Id}' is not in the session solution.");
        return project;
    }

    private static async Task<Compilation> CompileAsync(Project project, CancellationToken cancellationToken)
    {
        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Compilation was null for project '{project.Name}'.");
        return compilation;
    }
}
