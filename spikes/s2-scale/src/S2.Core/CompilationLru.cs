using Microsoft.CodeAnalysis;

namespace S2.Core;

/// <summary>
/// Explicit compilation holder with LRU eviction for measuring thrashing under capacity caps.
/// This is a harness simulation — not Roslyn's internal cache.
/// </summary>
public sealed class CompilationLru
{
    private readonly int? _capacity;
    private readonly LinkedList<ProjectId> _order = new();
    private readonly Dictionary<ProjectId, Compilation> _map = new();
    private int _evictions;

    public CompilationLru(int capacity)
    {
        // 0 or negative => unlimited
        _capacity = capacity <= 0 ? null : capacity;
    }

    public int Count => _map.Count;
    public int Evictions => _evictions;
    public int? Capacity => _capacity;

    public async Task<Compilation> GetOrAddAsync(
        Project project,
        CancellationToken ct = default)
    {
        if (_map.TryGetValue(project.Id, out var existing))
        {
            Touch(project.Id);
            return existing;
        }

        var compilation = await project.GetCompilationAsync(ct)
            ?? throw new InvalidOperationException($"Compilation was null for {project.Name}");

        if (_capacity is int cap && _map.Count >= cap)
        {
            var oldest = _order.Last!;
            _order.RemoveLast();
            _map.Remove(oldest.Value);
            _evictions++;
        }

        _map[project.Id] = compilation;
        _order.AddFirst(project.Id);
        return compilation;
    }

    public void Clear()
    {
        _map.Clear();
        _order.Clear();
    }

    private void Touch(ProjectId id)
    {
        var node = _order.Find(id);
        if (node is null)
        {
            return;
        }

        _order.Remove(node);
        _order.AddFirst(node);
    }
}
