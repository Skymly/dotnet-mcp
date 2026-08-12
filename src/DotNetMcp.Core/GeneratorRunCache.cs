using System.Collections.Concurrent;

namespace DotNetMcp.Core;

/// <summary>
/// Shared generator-driver snapshot cache keyed by (projectId, epoch).
/// Owned by the workspace host and cleared when the epoch advances.
/// </summary>
public sealed class GeneratorRunCache
{
    private readonly ConcurrentDictionary<(string ProjectId, long Epoch), DriverRunSnapshot> _cache = new();

    public bool TryGet(string projectId, long epoch, out DriverRunSnapshot snapshot) =>
        _cache.TryGetValue((projectId, epoch), out snapshot!);

    public void Set(string projectId, long epoch, DriverRunSnapshot snapshot) =>
        _cache[(projectId, epoch)] = snapshot;

    public void Clear() => _cache.Clear();
}
