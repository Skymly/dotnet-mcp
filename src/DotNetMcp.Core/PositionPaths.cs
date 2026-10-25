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

        return string.Equals(Normalize(left), Normalize(right), Comparison);
    }

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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
}
