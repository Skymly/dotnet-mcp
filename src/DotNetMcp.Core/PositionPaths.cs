namespace DotNetMcp.Core;

/// <summary>
/// File-path comparison for the position entry (ADR-0001 Amendment 7): same
/// canonical form the snapshot documents use — full path, OrdinalIgnoreCase
/// on Windows so generated/snapshot FilePaths match caller input verbatim.
/// </summary>
internal static class PositionPaths
{
    public static bool Equal(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        if (string.Equals(Normalize(left), Normalize(right), Comparison))
        {
            return true;
        }

        // Stored paths and caller input can differ by a symlinked ancestor
        // (macOS /var -> /private/var); canonical comparison settles identity.
        return string.Equals(Canonical(left), Canonical(right), Comparison);
    }

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Dedup/order key under the same canonical form as <see cref="Equal"/>:
    /// full path, case-folded on Windows. Shared/linked documents seen through
    /// two projects collapse to one key.
    /// </summary>
    public static string Key(string path)
    {
        var normalized = Normalize(path);
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception)
        {
            return path.Trim();
        }
    }

    private static string Canonical(string path)
    {
        try
        {
            return PathCanonical.Of(path.Trim());
        }
        catch (Exception)
        {
            return Normalize(path);
        }
    }
}
