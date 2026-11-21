using Microsoft.CodeAnalysis;

namespace DotNetMcp.Server;

/// <summary>
/// Post-load / pre-open checks that every project, document, and on-disk analyzer
/// path stays under trusted roots (documents and analyzers may also sit under
/// toolchain roots — see ADR-0004 Amendment 10).
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
                    $"{context}: a project path resolves outside the configured trusted roots and was rejected.",
                    projectPath);
            }
        }
    }

    /// <summary>
    /// After MSBuild / workspace load, reject any on-disk project, document, or
    /// analyzer whose final path escapes trusted roots (and, for documents and
    /// analyzers, toolchain roots). Project paths stay trusted-roots-only: a project
    /// inside the NuGet package cache is not a supported scenario.
    /// Documents under toolchain roots are admitted read-only — packages such as
    /// xunit v3 inject package-cache sources (e.g. <c>_content/*.cs</c>) into the
    /// compile, and MSBuild/analyzers already execute code from these roots on every
    /// build, so allowing READ is strictly weaker. The write set is unchanged:
    /// preview/apply still refuses paths outside trusted roots.
    /// Synthetic in-memory fixture paths that do not exist on disk are skipped
    /// so AdhocWorkspace tests keep working; real escaping files are still caught.
    /// Analyzer checks use <see cref="AnalyzerReference.FullPath"/> only — never
    /// <c>GetAnalyzers</c> / <c>GetGenerators</c>, which would load the DLL first.
    /// MetadataReferences are intentionally not checked (read-only PE metadata).
    /// </summary>
    public static void EnsureLoadedSolutionUnderRoots(LoadedSolution loaded, TrustedRoots trustedRoots)
    {
        var projects = loaded.Solution.Projects;
        var projectPaths = ExistingPaths(projects.Select(static p => p.FilePath));
        EnsureProjectPathsUnderRoots(projectPaths, trustedRoots, "workspace_open");

        var toolchainRoots = ToolchainRoots.Discover();
        var documentPaths = ExistingPaths(projects.SelectMany(static p =>
            p.Documents.Concat<TextDocument>(p.AdditionalDocuments).Select(static d => d.FilePath)));
        foreach (var path in documentPaths)
        {
            if (!IsTrustedOrToolchain(path, trustedRoots, toolchainRoots))
            {
                throw new LoadedGraphOutsideTrustedRootsException(
                    "workspace_open: a document path resolves outside the configured trusted roots and toolchain roots and was rejected.",
                    path);
            }
        }

        var analyzerPaths = ExistingPaths(projects.SelectMany(static p =>
            p.AnalyzerReferences.Select(static r => r.FullPath)));
        foreach (var path in analyzerPaths)
        {
            if (!IsTrustedOrToolchain(path, trustedRoots, toolchainRoots))
            {
                throw new LoadedGraphOutsideTrustedRootsException(
                    "workspace_open: an analyzer reference resolves outside the configured trusted roots and toolchain roots and was rejected.",
                    path);
            }
        }
    }

    private static IEnumerable<string> ExistingPaths(IEnumerable<string?> paths) => paths
        .Where(static path => !string.IsNullOrWhiteSpace(path))
        .Cast<string>()
        .Distinct(StringComparer.Ordinal)
        .Where(PathExists);

    private static bool IsTrustedOrToolchain(string path, TrustedRoots trustedRoots, ToolchainRoots toolchainRoots)
    {
        string normalized;
        try
        {
            normalized = PathPolicy.Normalize(path);
        }
        catch (Exception ex) when (ex is PathPolicyException or ArgumentException)
        {
            return false;
        }

        return trustedRoots.ContainsNormalized(normalized) || toolchainRoots.ContainsNormalized(normalized);
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
    public LoadedGraphOutsideTrustedRootsException(string message, string path) : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    public string Path { get; }
}
