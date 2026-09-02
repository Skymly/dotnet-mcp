namespace DotNetMcp.Server;

/// <summary>
/// Thrown when a reparse point / symlink / junction cannot be resolved to a final path.
/// Callers must treat this as fail-closed (path is not trusted).
/// </summary>
public sealed class PathPolicyException : IOException
{
    public PathPolicyException(string message) : base(message)
    {
    }

    public PathPolicyException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// Path normalization and prefix checks for trusted-root enforcement (ADR-0004).
/// Every existing path component is canonicalized (parent symlinks/junctions included).
/// Unresolvable reparse points fail closed via <see cref="PathPolicyException"/>.
/// </summary>
public static class PathPolicy
{
    private static readonly char[] DirectorySeparators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        return Canonicalize(full);
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

        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }

    /// <summary>
    /// Walk every path component from the volume root, resolving reparse points along the way.
    /// Non-existent trailing segments are appended to the last resolved existing prefix.
    /// </summary>
    private static string Canonicalize(string fullPath)
    {
        var trimmed = TrimTrailingSeparators(fullPath);
        var root = Path.GetPathRoot(trimmed);
        if (string.IsNullOrEmpty(root))
        {
            return trimmed;
        }

        var relative = trimmed.Length > root.Length
            ? trimmed[root.Length..].TrimStart(DirectorySeparators)
            : string.Empty;

        // Never trim the volume root to empty (Unix "/" must stay "/").
        var current = Path.GetFullPath(root);
        if (string.IsNullOrEmpty(relative))
        {
            return ResolveExistingNode(current);
        }

        var segments = relative.Split(DirectorySeparators, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var next = CombineUnder(current, segments[i]);
            if (NodeExistsOrIsReparse(next))
            {
                current = ResolveExistingNode(next);
                continue;
            }

            // Remaining segments do not exist yet — append lexically to the resolved prefix.
            for (var j = i; j < segments.Length; j++)
            {
                current = CombineUnder(current, segments[j]);
            }

            return TrimTrailingSeparators(Path.GetFullPath(current));
        }

        return TrimTrailingSeparators(Path.GetFullPath(current));
    }

    private static string ResolveExistingNode(string path)
    {
        var isReparse = IsReparsePoint(path);
        try
        {
            FileSystemInfo? target = null;
            if (Directory.Exists(path))
            {
                target = Directory.ResolveLinkTarget(path, returnFinalTarget: true);
            }
            else if (File.Exists(path) || isReparse)
            {
                target = File.ResolveLinkTarget(path, returnFinalTarget: true)
                    ?? Directory.ResolveLinkTarget(path, returnFinalTarget: true);
            }
            else
            {
                return TrimTrailingSeparators(Path.GetFullPath(path));
            }

            if (isReparse)
            {
                if (target is null)
                {
                    throw new PathPolicyException(
                        "A reparse point could not be resolved to a final target.");
                }

                var finalPath = TrimTrailingSeparators(Path.GetFullPath(target.FullName));
                // Dangling / incomplete link targets fail closed.
                if (!Directory.Exists(finalPath) && !File.Exists(finalPath))
                {
                    throw new PathPolicyException(
                        "A reparse point target does not exist; refusing to treat the path as trusted.");
                }

                return finalPath;
            }

            return TrimTrailingSeparators(Path.GetFullPath(target?.FullName ?? path));        }
        catch (PathPolicyException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (isReparse)
            {
                throw new PathPolicyException(
                    "A reparse point could not be followed while enforcing trusted roots.",
                    ex);
            }
        }

        return TrimTrailingSeparators(Path.GetFullPath(path));
    }

    private static bool NodeExistsOrIsReparse(string path) =>
        Directory.Exists(path) || File.Exists(path) || IsReparsePoint(path);

    private static bool IsReparsePoint(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Combine without treating an empty current as "relative to process CWD".
    /// </summary>
    private static string CombineUnder(string current, string segment)
    {
        if (string.IsNullOrEmpty(current))
        {
            return Path.DirectorySeparatorChar + segment;
        }

        return Path.Combine(current, segment);
    }

    /// <summary>
    /// Trim trailing separators but never drop below the volume root
    /// (so Unix "/" stays "/", and Windows "C:\" stays "C:\").
    /// </summary>
    private static string TrimTrailingSeparators(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        var root = Path.GetPathRoot(path);
        var trimmed = path.TrimEnd(DirectorySeparators);
        if (string.IsNullOrEmpty(root))
        {
            return trimmed;
        }

        if (trimmed.Length < root.Length)
        {
            return root;
        }

        return trimmed;
    }
}
