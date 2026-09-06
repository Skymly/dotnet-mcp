using System.Diagnostics.CodeAnalysis;

namespace DotNetMcp.Core;

/// <summary>
/// Same-Epoch flattened finder hits. Implementation cache, not a workspace index.
/// A newer epoch replaces the previous epoch's entries so the map cannot grow across generations.
/// </summary>
public sealed class FindHitCache
{
    private const int MaxEntries = 256;
    private readonly object _gate = new();
    private readonly Dictionary<(long Epoch, string Handle, string Scope), object> _map = new();
    private long? _epoch;

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
            if (_epoch != epoch)
            {
                _map.Clear();
                _epoch = epoch;
            }

            if (_map.Count >= MaxEntries)
            {
                _map.Clear();
            }

            _map[(epoch, handle, scope)] = byDocument;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _epoch = null;
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
