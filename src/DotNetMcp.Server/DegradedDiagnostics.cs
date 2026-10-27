using DotNetMcp.Core;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Server;

/// <summary>
/// Shared degraded-compilation shape (issue #546 / #560 / #566): the same three
/// signals — missing-metadata diagnostics (CS0006/CS0012), declared project
/// references that resolved to no usable metadata, and declared
/// analyzer/generator references that did not materialize — formatted once for
/// project_diagnostics and the diagnostics fix tools.
/// </summary>
internal static class DegradedDiagnostics
{
    /// <summary>One project's degraded state; <see cref="Degraded"/> mirrors the
    /// combination logic <see cref="ProjectDiagnosticsResult.Degraded"/> applies.</summary>
    internal sealed record State(
        int MissingMetadataDiagnosticCount,
        IReadOnlyList<string> UnmaterializedReferences,
        IReadOnlyList<string> UnresolvedAnalyzerReferences)
    {
        public bool Degraded =>
            MissingMetadataDiagnosticCount > 0 ||
            UnmaterializedReferences.Count > 0 ||
            UnresolvedAnalyzerReferences.Count > 0;
    }

    /// <summary>
    /// Best-effort degraded state for one project. Never throws: a project the
    /// query does not own or any resolution failure yields null so the caller
    /// can omit the fields rather than fail the tool; an unavailable
    /// compilation only drops the diagnostic-count signal — declared-input
    /// signals still report.
    /// </summary>
    internal static async Task<State?> TryComputeAsync(
        IWorkspaceSession session, string projectId, CancellationToken cancellationToken)
    {
        try
        {
            var project = session.Solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Id.Id.ToString("D"), projectId, StringComparison.OrdinalIgnoreCase));
            if (project is null)
            {
                return null;
            }

            var unmaterialized = ProjectReferenceMaterialization.FindUnmaterialized(project);
            var unresolvedAnalyzers = AnalyzerReferenceMaterialization.FindUnresolved(session, project);
            var missingMetadataCount = 0;
            try
            {
                var compilation = await session.GetCompilationAsync(project.Id, cancellationToken).ConfigureAwait(false);
                missingMetadataCount = compilation.GetDiagnostics(cancellationToken)
                    .Count(d => MissingMetadataDiagnostics.IsMissingMetadataDiagnostic(d.Id));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A project whose compilation cannot be produced — an F# project
                // under a fix tool, a failed compile — keeps its declared-input
                // signals; the count alone is lost.
            }

            return new State(missingMetadataCount, unmaterialized, unresolvedAnalyzers);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// degradedReason text shared by every surface carrying the flag. Distinguishes
    /// the three classes — missing metadata references (CS0006/CS0012), declared
    /// project references with no usable metadata, and declared analyzer/generator
    /// references that did not materialize — and keeps the verify-with-build
    /// guidance. A declared path cannot tell analyzer from source generator, so
    /// the reason names both: a missing generator can cause false CS0234/CS0103/CS1061.
    /// </summary>
    internal static string? ComposeReason(
        int missingMetadataDiagnosticCount,
        IReadOnlyList<string> unmaterializedReferences,
        IReadOnlyList<string>? unresolvedAnalyzerReferences = null)
    {
        var unresolvedAnalyzers = unresolvedAnalyzerReferences ?? (IReadOnlyList<string>)[];
        if (missingMetadataDiagnosticCount <= 0 &&
            unmaterializedReferences.Count <= 0 &&
            unresolvedAnalyzers.Count <= 0)
        {
            return null;
        }

        var causes = new List<string>(3);
        if (missingMetadataDiagnosticCount > 0)
        {
            causes.Add($"missing metadata references ({missingMetadataDiagnosticCount} CS0006/CS0012 diagnostic(s))");
        }

        if (unmaterializedReferences.Count > 0)
        {
            causes.Add($"unresolved project reference(s) with no usable metadata ({string.Join(", ", unmaterializedReferences)})");
        }

        if (unresolvedAnalyzers.Count > 0)
        {
            causes.Add(
                "declared analyzer/generator reference(s) did not materialize (" +
                string.Join(", ", unresolvedAnalyzers.Select(path => $"'{path}'")) + ")");
        }

        var generatorNote = unresolvedAnalyzers.Count > 0
            ? "; if any was a source generator, missing generated members may cause false CS0234/CS0103/CS1061 errors"
            : string.Empty;
        var remediation = (unmaterializedReferences.Count > 0, unresolvedAnalyzers.Count > 0) switch
        {
            (true, true) => "build the referenced and generator projects or restore the packages",
            (false, true) => "build the generator project or restore the package",
            _ => "build the referenced projects",
        };
        return $"Compilation did not resolve all compile inputs — {string.Join(" and ", causes)}; " +
            $"reported errors may be workspace artifacts{generatorNote} — " +
            $"{remediation}, then verify with dotnet build.";
    }
}
