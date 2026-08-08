namespace DotNetMcp.Server;

/// <summary>
/// Path normalization and prefix checks for trusted-root enforcement (ADR-0004).
/// </summary>
public static class PathPolicy
{
    private static readonly char[] DirectorySeparators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        return ResolveExistingChain(full);
    }

    public static bool IsUnderRoot(string normalizedPath, string normalizedRoot)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var path = TrimTrailingSeparators(normalizedPath);
        var root = TrimTrailingSeparators(normalizedRoot);

        if (string.Equals(path, root, comparison))
        {
            return true;
        }

        var prefix = root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }

    private static string ResolveExistingChain(string fullPath)
    {
        // Resolve reparse points / junctions / symlinks for the longest existing prefix.
        var candidate = TrimTrailingSeparators(fullPath);
        var suffix = new Stack<string>();

        while (true)
        {
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                var resolved = ResolveLinkTarget(candidate) ?? candidate;
                if (suffix.Count == 0)
                {
                    return TrimTrailingSeparators(Path.GetFullPath(resolved));
                }

                var combined = Path.Combine(new[] { resolved }.Concat(suffix.Reverse()).ToArray());
                return TrimTrailingSeparators(Path.GetFullPath(combined));
            }

            var parent = Path.GetDirectoryName(candidate);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, candidate, StringComparison.Ordinal))
            {
                return TrimTrailingSeparators(fullPath);
            }

            suffix.Push(Path.GetFileName(candidate));
            candidate = parent;
        }
    }

    private static string? ResolveLinkTarget(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var target = Directory.ResolveLinkTarget(path, returnFinalTarget: true);
                return target?.FullName;
            }

            if (File.Exists(path))
            {
                var target = File.ResolveLinkTarget(path, returnFinalTarget: true);
                return target?.FullName;
            }
        }
        catch (IOException)
        {
            // Fall back to the unresolved path when the link cannot be followed.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static string TrimTrailingSeparators(string path) =>
        path.TrimEnd(DirectorySeparators);
}
