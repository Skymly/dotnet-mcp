using System.Text;
using System.Text.Json;

namespace DotNetMcp.Core;

/// <summary>
/// Opaque find-references page cursor: workspace epoch + scope + document walk progress + TTL.
/// </summary>
public static class FindRefsPageCursor
{
    private const string Version = "v1";
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);

    private sealed record Payload(
        string V,
        long Epoch,
        bool EntireSolution,
        int DocIndex,
        int LocOffset,
        long IssuedAtUnixMs);

    public static string Encode(
        long epoch,
        bool entireSolution,
        int docIndex,
        int locOffset,
        DateTimeOffset? issuedAt = null)
    {
        var issued = (issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize(
            new Payload(Version, epoch, entireSolution, docIndex, locOffset, issued));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static bool TryDecode(
        string? cursor,
        out long epoch,
        out bool entireSolution,
        out int docIndex,
        out int locOffset,
        out string? error,
        DateTimeOffset? now = null,
        TimeSpan? ttl = null)
    {
        epoch = 0;
        entireSolution = false;
        docIndex = 0;
        locOffset = 0;
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
                payload.DocIndex < 0 ||
                payload.LocOffset < 0 ||
                payload.IssuedAtUnixMs <= 0)
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
            entireSolution = payload.EntireSolution;
            docIndex = payload.DocIndex;
            locOffset = payload.LocOffset;
            return true;
        }
        catch (Exception)
        {
            error = "Cursor could not be decoded.";
            return false;
        }
    }
}
