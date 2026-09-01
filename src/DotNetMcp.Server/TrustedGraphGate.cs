namespace DotNetMcp.Server;

/// <summary>
/// Post-load / pre-open checks that every project (and document) path stays under trusted roots.
/// </summary>
public static class TrustedGraphGate
{
    /// <summary>
    /// Hard-fail any declared project path that resolves outside trusted roots.
    /// Used for <c>.slnf</c> entries before MSBuild opens them (existence optional —
    /// absolute / <c>..</c> escapes must not be attempted).
    /// </summary>
    public static void EnsureProjectPathsUnderRoots(
        IEnumerable<string> projectPaths,
        TrustedRoots trustedRoots,
        string context)
    {
        foreach (var projectPath in projectPaths)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                continue;
            }

            if (!trustedRoots.Contains(projectPath))
            {
                throw new InvalidOperationException(
                    $"{context}: a project path resolves outside the configured trusted roots and was rejected.");
            }
        }
    }

    /// <summary>
    /// After MSBuild / workspace load, reject any on-disk project or document whose
    /// final path escapes trusted roots (ProjectReference / multi-project graphs).
    /// Synthetic in-memory fixture paths that do not exist on disk are skipped so
    /// AdhocWorkspace tests keep working; real escaping files are still caught.
    /// </summary>
    public static void EnsureLoadedSolutionUnderRoots(LoadedSolution loaded, TrustedRoots trustedRoots)
    {
        var projectPaths = loaded.Solution.Projects
            .Select(static p => p.FilePath)
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Cast<string>()
            .Where(PathExists);

        EnsureProjectPathsUnderRoots(projectPaths, trustedRoots, "workspace_open");

        foreach (var project in loaded.Solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (string.IsNullOrWhiteSpace(document.FilePath) || !PathExists(document.FilePath))
                {
                    continue;
                }

                if (!trustedRoots.Contains(document.FilePath))
                {
                    throw new InvalidOperationException(
                        "workspace_open: a document path resolves outside the configured trusted roots and was rejected.");
                }
            }
        }
    }

    private static bool PathExists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
