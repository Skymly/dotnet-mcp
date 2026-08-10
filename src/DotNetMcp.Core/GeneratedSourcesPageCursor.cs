using System.Text;
using System.Text.Json;

namespace DotNetMcp.Core;

/// <summary>
/// Opaque page cursor for generated-source lists. Keys include generator identity because HintName
/// is not unique across generators (ADR-0001 §6 / Spike S1 Q4).
/// </summary>
public static class GeneratedSourcesPageCursor
{
    private const string Version = "v1";
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);

    private sealed record Payload(
        string V,
        long Epoch,
        string AssemblyName,
        string TypeFullName,
        int Offset,
        long IssuedAtUnixMs);

    public static string Encode(
        long epoch,
        string assemblyName,
        string typeFullName,
        int offset,
        DateTimeOffset? issuedAt = null)
    {
        var issued = (issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize(new Payload(
            Version,
            epoch,
            assemblyName,
            typeFullName,
            offset,
            issued));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static bool TryDecode(
        string? cursor,
        out long epoch,
        out string assemblyName,
        out string typeFullName,
        out int offset,
        out string? error,
        DateTimeOffset? now = null,
        TimeSpan? ttl = null)
    {
        epoch = 0;
        assemblyName = string.Empty;
        typeFullName = string.Empty;
        offset = 0;
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
                string.IsNullOrWhiteSpace(payload.AssemblyName) ||
                string.IsNullOrWhiteSpace(payload.TypeFullName) ||
                payload.Offset < 0 ||
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
            assemblyName = payload.AssemblyName;
            typeFullName = payload.TypeFullName;
            offset = payload.Offset;
            return true;
        }
        catch (Exception)
        {
            error = "Cursor could not be decoded.";
            return false;
        }
    }
}
