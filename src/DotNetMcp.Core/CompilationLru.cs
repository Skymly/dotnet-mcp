using Microsoft.CodeAnalysis;

namespace DotNetMcp.Core;

/// <summary>
/// Per-session compilation cache with LRU eviction (ADR-0002 / Spike S2).
/// Capacity &lt;= 0 means unlimited.
/// </summary>
public sealed class CompilationLru
{
    private readonly int? _capacity;
    private readonly LinkedList<ProjectId> _order = new();
    private readonly Dictionary<ProjectId, Compilation> _map = new();

    public CompilationLru(int capacity)
    {
        _capacity = capacity <= 0 ? null : capacity;
    }

    public int Count => _map.Count;

    public int Evictions { get; private set; }

    public int? Capacity => _capacity;

    public async Task<Compilation> GetOrAddAsync(
        Project project,
        Func<Project, CancellationToken, Task<Compilation>> factory,
        CancellationToken cancellationToken = default)
    {
        if (_map.TryGetValue(project.Id, out var existing))
        {
            Touch(project.Id);
            return existing;
        }

        var compilation = await factory(project, cancellationToken).ConfigureAwait(false);

        if (_capacity is int cap && _map.Count >= cap)
        {
            var oldest = _order.Last!;
            _order.RemoveLast();
            _map.Remove(oldest.Value);
            Evictions++;
        }

        _map[project.Id] = compilation;
        _order.AddFirst(project.Id);
        return compilation;
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
