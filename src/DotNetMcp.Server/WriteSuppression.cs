namespace DotNetMcp.Server;

/// <summary>
/// Tracks paths the process is writing so FSW callbacks can ignore self-induced events (ADR-0002).
/// </summary>
public sealed class WriteSuppression
{
    private readonly object _gate = new();
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

    public IDisposable Suppress(params string[] paths) => Suppress((IEnumerable<string>)paths);

    public IDisposable Suppress(IEnumerable<string> paths)
    {
        var normalized = paths
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (_gate)
        {
            foreach (var path in normalized)
            {
                _paths.Add(path);
            }
        }

        return new Releaser(() =>
        {
            lock (_gate)
            {
                foreach (var path in normalized)
                {
                    _paths.Remove(path);
                }
            }
        });
    }

    public bool IsSuppressed(string path)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            return _paths.Contains(normalized);
        }
    }

    private static string Normalize(string path) => Path.GetFullPath(path);

    private sealed class Releaser(Action release) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                release();
            }
        }
    }
}
