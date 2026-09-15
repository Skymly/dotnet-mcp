using System.Text;
using System.Text.Json;

namespace DotNetMcp.Core;

/// <summary>
/// Opaque member-list page cursor: workspace epoch + tool + query identity + TTL (ADR-0001 §4).
/// </summary>
public static class MemberPageCursor
{
    private const string Version = "v2";
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);

    private sealed record Payload(
        string V,
        long Epoch,
        int Offset,
        long IssuedAtUnixMs,
        string Tool,
        string QueryId);

    public static string Encode(
        long epoch,
        int offset,
        string tool,
        string queryId,
        DateTimeOffset? issuedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        var issued = (issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize(
            new Payload(Version, epoch, offset, issued, tool, queryId ?? string.Empty));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static bool TryDecode(
        string? cursor,
        out long epoch,
        out int offset,
        out string tool,
        out string queryId,
        out string? error,
        DateTimeOffset? now = null,
        TimeSpan? ttl = null)
    {
        epoch = 0;
        offset = 0;
        tool = string.Empty;
        queryId = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            error = "Cursor is empty.";
            return false;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var payload = JsonSerializer.Deserialize<Payload>(json);
            if (payload is null ||
                !string.Equals(payload.V, Version, StringComparison.Ordinal) ||
                payload.Offset < 0 ||
                payload.IssuedAtUnixMs <= 0 ||
                string.IsNullOrWhiteSpace(payload.Tool))
            {
                error = "Cursor payload is invalid.";
                return false;
            }

            var clock = now ?? DateTimeOffset.UtcNow;
            var maxAge = ttl ?? DefaultTtl;
            var issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(payload.IssuedAtUnixMs);
            if (clock - issuedAt > maxAge)
            {
                error = "Cursor has expired (TTL exceeded).";
                return false;
            }

            epoch = payload.Epoch;
            offset = payload.Offset;
            tool = payload.Tool;
            queryId = payload.QueryId ?? string.Empty;
            return true;
        }
        catch (Exception)
        {
            error = "Cursor could not be decoded.";
            return false;
        }
    }
}
