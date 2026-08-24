using Microsoft.CodeAnalysis;

namespace DotNetMcp.Core;

/// <summary>
/// Workspace-owned compilation cache with LRU eviction (ADR-0002 / Spike S2).
/// Shared across request sessions of the same epoch; capacity &lt;= 0 means unlimited.
/// </summary>
public sealed class CompilationLru
{
    private readonly int? _capacity;
    private readonly LinkedList<ProjectId> _order = new();
    private readonly Dictionary<ProjectId, Compilation> _map = new();
    private readonly object _gate = new();

    public CompilationLru(int capacity)
    {
        _capacity = capacity <= 0 ? null : capacity;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    public int Hits { get; private set; }

    public int Misses { get; private set; }

    public int Evictions { get; private set; }

    public int? Capacity => _capacity;

    public bool TryGet(ProjectId id, out Compilation compilation)
    {
        lock (_gate)
        {
            return TryGetAndTouch(id, out compilation);
        }
    }

    public async Task<Compilation> GetOrAddAsync(
        Project project,
        Func<Project, CancellationToken, Task<Compilation>> factory,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (TryGetAndTouch(project.Id, out var existing))
            {
                Hits++;
                return existing;
            }

            Misses++;
        }

        var compilation = await factory(project, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (TryGetAndTouch(project.Id, out var existing))
            {
                Hits++;
                return existing;
            }

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
    }

    private bool TryGetAndTouch(ProjectId id, out Compilation compilation)
    {
        if (_map.TryGetValue(id, out compilation!))
        {
            Touch(id);
            return true;
        }

        compilation = null!;
        return false;
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
