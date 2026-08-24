using System.Diagnostics.CodeAnalysis;

namespace DotNetMcp.Core;

/// <summary>
/// Same-Epoch flattened finder hits. Implementation cache, not a workspace index.
/// </summary>
public sealed class FindHitCache
{
    private readonly object _gate = new();
    private readonly Dictionary<(long Epoch, string Handle, string Scope), object> _map = new();

    public bool TryGetByDocument<T>(
        long epoch,
        string handle,
        string scope,
        [NotNullWhen(true)] out IReadOnlyList<IReadOnlyList<T>>? byDocument)
    {
        lock (_gate)
        {
            if (_map.TryGetValue((epoch, handle, scope), out var boxed) &&
                boxed is IReadOnlyList<IReadOnlyList<T>> typed)
            {
                byDocument = typed;
                return true;
            }
        }

        byDocument = null;
        return false;
    }

    public void SetByDocument<T>(
        long epoch,
        string handle,
        string scope,
        IReadOnlyList<IReadOnlyList<T>> byDocument)
    {
        ArgumentNullException.ThrowIfNull(byDocument);
        lock (_gate)
        {
            _map[(epoch, handle, scope)] = byDocument;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
        }
    }
}

/// <summary>
/// Host-owned caches shared across request sessions of one Epoch.
/// Not part of the public MCP / <see cref="IWorkspaceSession"/> contract.
/// </summary>
public interface IWorkspaceSessionCaches
{
    CompilationLru CompilationCache { get; }

    FindHitCache FindHits { get; }
}
