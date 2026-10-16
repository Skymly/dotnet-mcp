namespace DotNetMcp.Core;

/// <summary>
/// The real path of a file or directory, with symlinks and junctions resolved segment by segment.
/// macOS keeps <c>/var</c> as a symlink to <c>/private/var</c>, so <c>Path.GetTempPath()</c> returns
/// <c>/var/folders/...</c> while the filesystem reports <c>/private/var/folders/...</c>.
/// <see cref="Directory.ResolveLinkTarget(string, bool)"/> only resolves a path that is itself a link,
/// so a single call on <c>/var/folders/...</c> returns null and the ancestor symlink survives.
/// A missing final segment stays lexical.
/// </summary>
public static class PathCanonical
{
    // Matches the symlink traversal cap ResolveLinkTarget uses on Unix.
    private const int MaxDepth = 40;

    public static string Of(string path) => Resolve(path, depth: 0, strict: false);

    /// <summary>
    /// Like <see cref="Of"/>, but a link whose target cannot be read fails instead of being kept as
    /// written. Trusted-root checks need that: a dangling link must not be treated as a real path.
    /// </summary>
    /// <exception cref="IOException">A link target could not be read.</exception>
    public static string OfOrThrow(string path) => Resolve(path, depth: 0, strict: true);

    private static string Resolve(string path, int depth, bool strict)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (depth > MaxDepth)
        {
            return Path.GetFullPath(path);
        }

        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return full;
        }

        var segments = full[root.Length..]
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            string? target;
            try
            {
                target = ReadLinkTarget(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (strict)
                {
                    throw;
                }

                return current;
            }

            if (target is null)
            {
                continue;
            }

            // LinkTarget is stored as written. A relative target (macOS stores /var as "private/var")
            // is relative to the link's own directory, not to the process working directory.
            if (!Path.IsPathRooted(target))
            {
                var parent = Path.GetDirectoryName(current);
                target = string.IsNullOrEmpty(parent) ? target : Path.Combine(parent, target);
            }

            if (strict && !LinkTargetExists(target))
            {
                // A dangling link has no final path. Trusted-root checks must fail closed on it
                // rather than accept the path the link spells out.
                throw new IOException($"The link '{current}' points at '{target}', which does not exist.");
            }

            // A link target is stored as written, so it can carry its own unresolved ancestors
            // (a link to /var/... still has /var -> /private/var in front of it). Resolve again.
            var resolved = Resolve(target, depth + 1, strict);
            for (var j = i + 1; j < segments.Length; j++)
            {
                resolved = Path.Combine(resolved, segments[j]);
            }

            return Resolve(resolved, depth + 1, strict);
        }

        return current;
    }

    private static bool LinkTargetExists(string target)
    {
        try
        {
            return Directory.Exists(target) || File.Exists(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The link target exactly as stored, or null when the path is not a link.
    /// <see cref="FileSystemInfo.LinkTarget"/> reads the link itself, unlike
    /// <see cref="File.GetAttributes(string)"/>, which on macOS reports a path under a symlinked
    /// ancestor as an ordinary directory.
    /// </summary>
    private static string? ReadLinkTarget(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return new DirectoryInfo(path).LinkTarget;
            }

            if (File.Exists(path))
            {
                return new FileInfo(path).LinkTarget;
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw;
        }

        return null;
    }
}
