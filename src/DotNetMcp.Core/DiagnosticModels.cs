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
    int MissingMetadataDiagnosticCount)
{
    /// <summary>
    /// True when the compilation reported CS0006/CS0012 — metadata references did
    /// not resolve, so listed errors may be workspace artifacts, not code defects.
    /// </summary>
    public bool Degraded => MissingMetadataDiagnosticCount > 0;
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
