namespace DotNetMcp.Core;

/// <summary>
/// The real path of a file or directory, with symlinks and junctions in the ancestors resolved.
/// macOS keeps <c>/var</c> as a symlink to <c>/private/var</c>, so the path <c>Path.GetTempPath()</c> returns and the
/// path the filesystem reports differ; comparing them as text misses. A missing final segment stays lexical.
/// </summary>
public static class PathCanonical
{
    public static string Of(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return full;
        }

        var segments = full[root.Length..]
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            var target = ResolveExistingLink(current);
            if (target is not null)
            {
                current = target.FullName;
            }
        }

        return current;
    }

    private static FileSystemInfo? ResolveExistingLink(string path)
    {
        if (Directory.Exists(path))
        {
            return Directory.ResolveLinkTarget(path, returnFinalTarget: true);
        }

        if (File.Exists(path))
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true);
        }

        return null;
    }
}
