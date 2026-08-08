using System.Collections.Concurrent;
using System.Text.Json;

namespace S3.Core;

/// <summary>
/// Thread-safe observation log for concurrency / cancel / progress experiments.
/// </summary>
public sealed class ObservationLog
{
    private readonly ConcurrentQueue<ObservationEntry> _entries = new();

    public void Write(string category, string message, object? data = null)
    {
        _entries.Enqueue(new ObservationEntry
        {
            Utc = DateTimeOffset.UtcNow,
            Category = category,
            Message = message,
            Data = data is null ? null : JsonSerializer.SerializeToElement(data, JsonOptions.Default),
            ThreadId = Environment.CurrentManagedThreadId
        });
    }

    public IReadOnlyList<ObservationEntry> Snapshot() => _entries.ToArray();

    public string ToJsonLines()
    {
        var lines = Snapshot().Select(e => JsonSerializer.Serialize(e, JsonOptions.Default));
        return string.Join(Environment.NewLine, lines);
    }

    public void Clear()
    {
        while (_entries.TryDequeue(out _))
        {
        }
    }
}

public sealed class ObservationEntry
{
    public DateTimeOffset Utc { get; init; }
    public required string Category { get; init; }
    public required string Message { get; init; }
    public JsonElement? Data { get; init; }
    public int ThreadId { get; init; }
}
