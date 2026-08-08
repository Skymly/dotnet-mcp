using Microsoft.CodeAnalysis;

namespace DotNetMcp.Server;

public sealed record LoadProgress(int CompletedUnits, int TotalUnits);

public sealed class LoadedSolution : IAsyncDisposable
{
    private readonly Workspace _workspace;

    public LoadedSolution(
        Workspace workspace,
        Solution solution,
        IReadOnlyList<string> warnings)
    {
        _workspace = workspace;
        Solution = solution;
        Warnings = warnings;
    }

    public Solution Solution { get; }
    public IReadOnlyList<string> Warnings { get; }

    public ValueTask DisposeAsync()
    {
        if (_workspace is IDisposable disposable)
        {
            disposable.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}

public interface ISolutionLoader
{
    Task<LoadedSolution> OpenAsync(
        string path,
        IProgress<LoadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
