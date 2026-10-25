using System.Diagnostics.CodeAnalysis;

namespace DotNetMcp.Core;

/// <summary>
/// Same-Epoch XAML usage points keyed by (Epoch, ProjectId), for the ADR-0006
/// usage index. Implementation cache, not a workspace index. A newer epoch
/// replaces the previous epoch's entries so the map cannot grow across
/// generations; the host clears it when the epoch advances.
/// </summary>
public sealed class XamlUsageIndexCache
{
    private const int MaxEntries = 64;
    private readonly object _gate = new();
    private readonly Dictionary<(long Epoch, string ProjectId), IReadOnlyList<XamlUsagePoint>> _map = new();
    private long? _epoch;

    public bool TryGet(
        long epoch,
        string projectId,
        [NotNullWhen(true)] out IReadOnlyList<XamlUsagePoint>? usages)
    {
        lock (_gate)
        {
            if (_map.TryGetValue((epoch, projectId), out var found))
            {
                usages = found;
                return true;
            }
        }

        usages = null;
        return false;
    }

    public void Set(long epoch, string projectId, IReadOnlyList<XamlUsagePoint> usages)
    {
        ArgumentNullException.ThrowIfNull(usages);
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

            _map[(epoch, projectId)] = usages;
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
