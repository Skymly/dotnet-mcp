using System.Collections.Concurrent;

namespace DotNetMcp.Core;

/// <summary>
/// Remembers the epoch of the last successful action list for a locator.
/// Preview must not reuse an index after that epoch moves.
/// Keyed by locator, not by session: each tool call gets a new session.
/// </summary>
internal sealed class ActionIndexLedger
{
    private readonly ConcurrentDictionary<string, long> _epochs = new();

    public void Remember(string locator, long epoch) => _epochs[locator] = epoch;

    public bool SnapshotMoved(string locator, long epoch) =>
        _epochs.TryGetValue(locator, out var listed) && listed != epoch;
}