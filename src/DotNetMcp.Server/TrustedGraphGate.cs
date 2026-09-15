using Microsoft.CodeAnalysis;

namespace DotNetMcp.Server;

/// <summary>
/// Post-load / pre-open checks that every project, document, and on-disk analyzer
/// path stays under trusted roots (analyzers may also sit under toolchain roots).
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
                throw new LoadedGraphOutsideTrustedRootsException(
                    $"{context}: a project path resolves outside the configured trusted roots and was rejected.");
            }
        }
    }

    /// <summary>
    /// After MSBuild / workspace load, reject any on-disk project, document, or
    /// analyzer whose final path escapes trusted roots (and, for analyzers, toolchain
    /// roots). Synthetic in-memory fixture paths that do not exist on disk are skipped
    /// so AdhocWorkspace tests keep working; real escaping files are still caught.
    /// Analyzer checks use <see cref="AnalyzerReference.FullPath"/> only — never
    /// <c>GetAnalyzers</c> / <c>GetGenerators</c>, which would load the DLL first.
    /// MetadataReferences are intentionally not checked (read-only PE metadata).
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
            foreach (var document in EnumerateOnDiskTextDocuments(project))
            {
                if (!trustedRoots.Contains(document.FilePath!))
                {
                    throw new LoadedGraphOutsideTrustedRootsException(
                        "workspace_open: a document path resolves outside the configured trusted roots and was rejected.");
                }
            }
        }

        var toolchainRoots = ToolchainRoots.Discover();
        foreach (var project in loaded.Solution.Projects)
        {
            foreach (var analyzer in project.AnalyzerReferences)
            {
                var fullPath = analyzer.FullPath;
                if (string.IsNullOrWhiteSpace(fullPath) || !PathExists(fullPath))
                {
                    continue;
                }

                if (trustedRoots.Contains(fullPath) || toolchainRoots.Contains(fullPath))
                {
                    continue;
                }

                throw new LoadedGraphOutsideTrustedRootsException(
                    "workspace_open: an analyzer reference resolves outside the configured trusted roots and toolchain roots and was rejected.");
            }
        }
    }

    private static IEnumerable<TextDocument> EnumerateOnDiskTextDocuments(Project project)
    {
        foreach (var document in project.Documents)
        {
            if (!string.IsNullOrWhiteSpace(document.FilePath) && PathExists(document.FilePath))
            {
                yield return document;
            }
        }

        foreach (var document in project.AdditionalDocuments)
        {
            if (!string.IsNullOrWhiteSpace(document.FilePath) && PathExists(document.FilePath))
            {
                yield return document;
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

public sealed class LoadedGraphOutsideTrustedRootsException : InvalidOperationException
{
    public LoadedGraphOutsideTrustedRootsException(string message) : base(message)
    {
    }
}
