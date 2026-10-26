using System.Xml.Linq;

namespace DotNetMcp.Server;

/// <summary>
/// Enumerates every <c>&lt;Project Path="…"&gt;</c> entry declared by a .slnx file, at any
/// solution-folder depth. MSBuildWorkspace's slnx reader silently drops projects nested
/// inside a <c>&lt;Folder&gt;</c> within a <c>&lt;Folder&gt;</c> (issue #545), so the loader
/// reconciles this expected set against what actually loaded. This is deliberately an
/// expected-set enumerator, not a full slnx parser: <c>&lt;File&gt;</c> entries and build
/// configuration metadata are ignored.
/// </summary>
public static class SlnxProjectEnumerator
{
    /// <summary>
    /// Returns the declared project paths as canonical absolute paths (relative entries are
    /// resolved against the .slnx directory), deduplicated under the platform path rules.
    /// </summary>
    public static IReadOnlyList<string> EnumerateProjectPaths(string slnxPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slnxPath);
        var full = Path.GetFullPath(slnxPath);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException("Solution file not found.", full);
        }

        var baseDir = Path.GetDirectoryName(full)
            ?? throw new InvalidOperationException("Cannot resolve .slnx directory.");
        var document = XDocument.Load(full);

        var results = new List<string>();
        var seen = new HashSet<string>(PathPolicy.Comparer);
        foreach (var element in document.Descendants("Project"))
        {
            var declared = (string?)element.Attribute("Path");
            if (string.IsNullOrWhiteSpace(declared))
            {
                continue;
            }

            var canonical = Canonicalize(Path.GetFullPath(Path.Combine(baseDir, declared)));
            if (seen.Add(canonical))
            {
                results.Add(canonical);
            }
        }

        return results;
    }

    /// <summary>
    /// Canonical comparison form: <see cref="PathPolicy.Normalize"/> where possible (reparse
    /// points resolved), falling back to the lexical full path so comparisons still work.
    /// </summary>
    internal static string Canonicalize(string path)
    {
        try
        {
            return PathPolicy.Normalize(path);
        }
        catch (Exception ex) when (ex is PathPolicyException or ArgumentException)
        {
            return Path.GetFullPath(path);
        }
    }
}
