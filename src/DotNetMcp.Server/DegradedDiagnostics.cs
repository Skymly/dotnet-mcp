using DotNetMcp.Core;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Server;

/// <summary>
/// Shared degraded-compilation shape (issue #546 / #560): the same two signals —
/// missing-metadata diagnostics (CS0006/CS0012) and declared project references
/// that resolved to no usable metadata — formatted once for project_diagnostics
/// and the diagnostics fix tools.
/// </summary>
internal static class DegradedDiagnostics
{
    /// <summary>
    /// Best-effort degraded state for one project. Never throws: a project the
    /// query does not own, an unavailable compilation, or any resolution failure
    /// yields null so the caller can omit the fields rather than fail the tool.
    /// </summary>
    internal static async Task<(int MissingMetadataDiagnosticCount, IReadOnlyList<string> UnmaterializedReferences)?>
        TryComputeAsync(IWorkspaceSession session, string projectId, CancellationToken cancellationToken)
    {
        try
        {
            var project = session.Solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Id.Id.ToString("D"), projectId, StringComparison.OrdinalIgnoreCase));
            if (project is null)
            {
                return null;
            }

            var compilation = await session.GetCompilationAsync(project.Id, cancellationToken).ConfigureAwait(false);
            var missingMetadataCount = compilation.GetDiagnostics(cancellationToken)
                .Count(d => MissingMetadataDiagnostics.IsMissingMetadataDiagnostic(d.Id));
            return (missingMetadataCount, ProjectReferenceMaterialization.FindUnmaterialized(project));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// degradedReason text shared by every surface carrying the flag. Distinguishes
    /// the two classes — missing metadata references (CS0006/CS0012) vs declared
    /// project references with no usable metadata — and keeps the verify-with-build
    /// guidance.
    /// </summary>
    internal static string? ComposeReason(
        int missingMetadataDiagnosticCount,
        IReadOnlyList<string> unmaterializedReferences)
    {
        if (missingMetadataDiagnosticCount <= 0 && unmaterializedReferences.Count <= 0)
        {
            return null;
        }

        var causes = new List<string>(2);
        if (missingMetadataDiagnosticCount > 0)
        {
            causes.Add($"missing metadata references ({missingMetadataDiagnosticCount} CS0006/CS0012 diagnostic(s))");
        }

        if (unmaterializedReferences.Count > 0)
        {
            causes.Add($"unresolved project reference(s) with no usable metadata ({string.Join(", ", unmaterializedReferences)})");
        }

        return $"Compilation did not resolve all compile inputs — {string.Join(" and ", causes)}; " +
            "reported errors may be workspace artifacts — build the referenced projects, then verify with dotnet build.";
    }
}
