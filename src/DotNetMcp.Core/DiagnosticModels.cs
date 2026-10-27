using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

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
/// project_diagnostics payload (issue #546): the paged items plus the degraded
/// signals computed over the FULL workspace state — missing-metadata diagnostics
/// in the full diagnostic set, unmaterialized project references, unresolved
/// analyzer/generator paths — the flag must not depend on which page slice was
/// returned.
/// </summary>
public sealed record ProjectDiagnosticsResult(
    PagedResult<DiagnosticItem> Page,
    int MissingMetadataDiagnosticCount,
    IReadOnlyList<string>? UnmaterializedProjectReferences = null,
    IReadOnlyList<string>? UnresolvedAnalyzerReferences = null)
{
    /// <summary>
    /// Directly declared project references whose targets produced no usable
    /// metadata for this compilation (issue #560) — the referenced project names
    /// or, for targets that never loaded, their declared project paths. Empty
    /// when every declared project reference materialized.
    /// </summary>
    public IReadOnlyList<string> UnmaterializedReferences => UnmaterializedProjectReferences ?? [];

    /// <summary>
    /// Analyzer/source-generator paths MSBuild declared for the project that no
    /// file materialized for (issue #566) — an OutputItemType="Analyzer" project
    /// reference whose output is not built, or a package analyzer path that does
    /// not exist. From a path alone analyzer vs generator cannot be told apart;
    /// a missing generator means missing generated members, so cascade
    /// CS0234/CS0103/CS1061 errors may be workspace artifacts. Empty when every
    /// declared analyzer reference resolved.
    /// </summary>
    public IReadOnlyList<string> UnresolvedAnalyzers => UnresolvedAnalyzerReferences ?? [];

    /// <summary>
    /// True when the compilation is missing compile inputs: it reported
    /// CS0006/CS0012 — metadata references did not resolve — or declared
    /// project references resolved to no usable metadata, or declared
    /// analyzer/generator references did not materialize. In every case listed
    /// errors may be workspace artifacts, not code defects.
    /// </summary>
    public bool Degraded =>
        MissingMetadataDiagnosticCount > 0 ||
        UnmaterializedReferences.Count > 0 ||
        UnresolvedAnalyzers.Count > 0;
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

/// <summary>
/// Declared-but-unmaterialized analyzer/generator detection (issue #566). Two
/// provenance shapes exist: the loader records the declared paths on the
/// session when it detaches the unchecksumable
/// <see cref="UnresolvedAnalyzerReference"/> stubs (issue #548 —
/// <see cref="IWorkspaceSession.DeclaredUnresolvedAnalyzerPaths"/>), and a
/// session without loader provenance can still carry the stub itself or a
/// file-backed reference whose dll vanished after open — the same unresolved
/// criterion project_list_generators applies (issue #551). The union is the
/// degraded signal: the declared path names a compile input MSBuild promised
/// and nothing materialized, which for a source generator means its generated
/// members never existed. Best-effort throughout — any lookup failure yields
/// what could still be proven rather than failing the diagnostics query.
/// </summary>
public static class AnalyzerReferenceMaterialization
{
    /// <summary>
    /// Analyzer/generator paths MSBuild declared for <paramref name="project"/>
    /// that no file materialized for: the session-recorded paths merged with
    /// unresolvable references still attached to the project.
    /// </summary>
    public static IReadOnlyList<string> FindUnresolved(IWorkspaceSession session, Project project)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(project);

        var paths = new SortedSet<string>(PathComparer());
        AddDeclaredPaths(session, project.Id, paths);
        AddUnmaterializedReferences(project, paths);
        return [.. paths];
    }

    /// <summary>
    /// Same lookup keyed by the string project id the F# snapshot carries.
    /// Falls back to session-declared data alone when the id is not a Roslyn
    /// project in this session's solution.
    /// </summary>
    public static IReadOnlyList<string> FindUnresolved(IWorkspaceSession session, string projectId)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            var project = session.Solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Id.Id.ToString("D"), projectId, StringComparison.OrdinalIgnoreCase));
            if (project is not null)
            {
                return FindUnresolved(session, project);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fall through to the session-declared path lookup.
        }

        if (!Guid.TryParse(projectId, out var guid))
        {
            return [];
        }

        var paths = new SortedSet<string>(PathComparer());
        AddDeclaredPaths(session, ProjectId.CreateFromSerialized(guid), paths);
        return [.. paths];
    }

    private static void AddDeclaredPaths(IWorkspaceSession session, ProjectId projectId, ISet<string> paths)
    {
        try
        {
            foreach (var declared in session.DeclaredUnresolvedAnalyzerPaths(projectId) ?? [])
            {
                if (!string.IsNullOrWhiteSpace(declared))
                {
                    paths.Add(declared);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Session provenance must never fail the diagnostics query.
        }
    }

    private static void AddUnmaterializedReferences(Project project, ISet<string> paths)
    {
        try
        {
            foreach (var reference in project.AnalyzerReferences)
            {
                string? fullPath;
                try
                {
                    fullPath = reference.FullPath;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    continue;
                }

                // No GetGenerators probe — declaring an analyzer that never
                // materialized is the signal regardless of what it would have
                // contained, and probing would load assemblies diagnostics never
                // needed before.
                if (!string.IsNullOrWhiteSpace(fullPath) &&
                    (reference is UnresolvedAnalyzerReference || !File.Exists(fullPath)))
                {
                    paths.Add(fullPath!);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: keep whatever was already proven.
        }
    }

    // Mirrors PathPolicy's comparison in DotNetMcp.Server, which Core cannot reference.
    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
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
