using Microsoft.CodeAnalysis;

namespace DotNetMcp.Core;

public sealed record DiagnosticItem(
    string Id,
    string Severity,
    string Message,
    string? FilePath,
    int? Line,
    int? Column,
    int? EndLine,
    int? EndColumn,
    string ProjectId,
    string? Error = null,
    string? SuggestedAction = null);

/// <summary>
/// project_diagnostics payload (issue #546): the paged items plus the count of
/// missing-metadata diagnostics in the FULL diagnostic set — the degraded signal
/// must not depend on which page slice was returned.
/// </summary>
public sealed record ProjectDiagnosticsResult(
    PagedResult<DiagnosticItem> Page,
    int MissingMetadataDiagnosticCount,
    IReadOnlyList<string>? UnmaterializedProjectReferences = null)
{
    /// <summary>
    /// Directly declared project references whose targets produced no usable
    /// metadata for this compilation (issue #560) — the referenced project names
    /// or, for targets that never loaded, their declared project paths. Empty
    /// when every declared project reference materialized.
    /// </summary>
    public IReadOnlyList<string> UnmaterializedReferences => UnmaterializedProjectReferences ?? [];

    /// <summary>
    /// True when the compilation is missing compile inputs: either it reported
    /// CS0006/CS0012 — metadata references did not resolve — or one or more
    /// declared project references resolved to no usable metadata. In both
    /// cases listed errors may be workspace artifacts, not code defects.
    /// </summary>
    public bool Degraded =>
        MissingMetadataDiagnosticCount > 0 || UnmaterializedReferences.Count > 0;
}

/// <summary>
/// Locale-independent degraded-workspace detection (issue #546). MSBuildWorkspace
/// "project reference without matching metadata reference" load warnings are
/// localized, so detection keys on the diagnostics missing references produce:
/// CS0006 (metadata file could not be found) and CS0012 (type defined in an
/// assembly that is not referenced). CS0234/CS0246 are deliberately excluded —
/// ambiguous with real typos.
/// </summary>
public static class MissingMetadataDiagnostics
{
    /// <summary>CS0006 — metadata file could not be found.</summary>
    public const string MetadataFileNotFoundId = "CS0006";

    /// <summary>CS0012 — type is defined in an assembly that is not referenced.</summary>
    public const string UnreferencedAssemblyTypeId = "CS0012";

    public static bool IsMissingMetadataDiagnostic(string? id) =>
        string.Equals(id, MetadataFileNotFoundId, StringComparison.Ordinal) ||
        string.Equals(id, UnreferencedAssemblyTypeId, StringComparison.Ordinal);

    public static int CountIn(IEnumerable<DiagnosticItem> items) =>
        items.Count(item => IsMissingMetadataDiagnostic(item.Id));
}

/// <summary>
/// Locale-independent degraded-workspace detection for failed project references
/// (issue #560). The MSBuildWorkspace "project reference without matching
/// metadata reference" load warning is localized text, so detection keys on the
/// structure the loaded workspace exposes, mirroring what Roslyn's compilation
/// pipeline does with each declared <see cref="ProjectReference"/>. Note
/// <see cref="Project.ProjectReferences"/> is filtered to references whose
/// target loaded, so detection walks <see cref="Project.AllProjectReferences"/>
/// — the full declared set — and for each one: a target id with no loaded
/// project was left by the loader for a reference it could not resolve and
/// contributes no metadata; a loaded target that does not support compilation
/// (for example an F# project, which has no Roslyn compilation) only contributes
/// its output dll path as a metadata reference, which is usable only when that
/// file exists on disk; a compilable target always materializes as a
/// compilation/metadata reference. CS0234/CS0246/CS1061 diagnostics cascaded by
/// the missing metadata stay ambiguous with real typos and are NOT evidence
/// themselves — the flag is the declared-but-unmaterialized reference.
/// </summary>
public static class ProjectReferenceMaterialization
{
    /// <summary>
    /// Names the <paramref name="project"/>'s declared project references whose
    /// targets produced no usable metadata for its compilation: the referenced
    /// project's name when it loaded, otherwise the unresolved target id — whose
    /// <see cref="ProjectId.ToString"/> carries the referenced project path
    /// MSBuildWorkspace recorded when it could not resolve the reference.
    /// </summary>
    public static IReadOnlyList<string> FindUnmaterialized(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var missing = new List<string>();
        foreach (var reference in project.AllProjectReferences)
        {
            var target = project.Solution.GetProject(reference.ProjectId);
            if (target is null)
            {
                missing.Add(reference.ProjectId.ToString());
            }
            else if (!target.SupportsCompilation &&
                (string.IsNullOrWhiteSpace(target.OutputFilePath) || !File.Exists(target.OutputFilePath)))
            {
                missing.Add(target.Name);
            }
        }

        return missing;
    }
}

public sealed record ProjectNotFoundError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.ProjectNotFound, Message, SuggestedAction);

public sealed record CompilationUnavailableError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.CompilationUnavailable, Message, SuggestedAction);

public sealed record GeneratorNotFoundError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.GeneratorNotFound, Message, SuggestedAction);

public sealed record GeneratorLanguageNotSupportedError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.GeneratorLanguageNotSupported, Message, SuggestedAction);

public sealed record DynamicInvocationLanguageNotSupportedError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.DynamicInvocationLanguageNotSupported, Message, SuggestedAction);

public sealed record GeneratorAttributionAmbiguousError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.GeneratorAttributionAmbiguous, Message, SuggestedAction);

public sealed record SoftBudgetExceededError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.SoftBudgetExceeded, Message, SuggestedAction);
