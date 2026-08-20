namespace DotNetMcp.Core;

public static class InteropKinds
{
    public const string None = "None";
    public const string ComImport = "ComImport";
    public const string ComInteropWrapper = "ComInteropWrapper";
}

public sealed record SymbolSummary(
    string Kind,
    string DisplayName,
    string? ContainingSymbol,
    string Accessibility,
    string ProjectId,
    string Language,
    string InteropKind = InteropKinds.None);

public sealed record SymbolResolveSuccess(string Handle, SymbolSummary Summary);

public sealed record SymbolLocation(
    string DeclarationAvailability,
    string? Origin,
    string? FilePath,
    int? Start,
    int? Length);

public sealed record SymbolDefinitionSuccess(IReadOnlyList<SymbolLocation> Locations);

public sealed record MemberListItem(string Handle, SymbolSummary Summary);

public sealed record ImplementationItem(
    string Handle,
    SymbolSummary Summary,
    IReadOnlyList<SymbolLocation> Locations);

public static class HierarchyRelationKind
{
    public const string BaseType = "BaseType";
    public const string Interface = "Interface";
}

public sealed record HierarchyItem(string Kind, string Handle, SymbolSummary Summary);

public sealed record ReferenceLocationItem(
    string DeclarationAvailability,
    string? Origin,
    string? FilePath,
    int? Start,
    int? Length,
    string ProjectId,
    string Kind);

public sealed record CallerLocationItem(
    string DeclarationAvailability,
    string? Origin,
    string? FilePath,
    int? Start,
    int? Length,
    string ProjectId,
    string CallerHandle,
    SymbolSummary CallerSummary);

public static class ReferenceLocationKind
{
    public const string Definition = "Definition";
    public const string Reference = "Reference";
}

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    bool Truncated,
    string? NextCursor,
    string Message);

public abstract record SymbolQueryError(string Code, string Message, string SuggestedAction);

public sealed record InvalidSymbolHandleError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.InvalidSymbolHandle, Message, SuggestedAction);

public sealed record SymbolNotFoundError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.SymbolNotFound, Message, SuggestedAction);

public sealed record SymbolAmbiguousError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.SymbolAmbiguous, Message, SuggestedAction);

public sealed record StaleCursorError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.StaleCursor, Message, SuggestedAction);

public sealed record DefinitionNotFoundError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.DefinitionNotFound, Message, SuggestedAction);

public sealed record MemberNotFoundError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.MemberNotFound, Message, SuggestedAction);

public sealed record GeneratedSymbolRenameRefusedError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.GeneratedSymbolRenameRefused, Message, SuggestedAction);

public sealed record RenameLanguageNotSupportedError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.RenameLanguageNotSupported, Message, SuggestedAction);

public sealed record InvalidRenameNameError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.InvalidRenameName, Message, SuggestedAction);

public static class SymbolQueryErrorCodes
{
    public const string InvalidSymbolHandle = "InvalidSymbolHandle";
    public const string SymbolNotFound = "SymbolNotFound";
    public const string SymbolAmbiguous = "SymbolAmbiguous";
    public const string StaleCursor = "StaleCursor";
    public const string DefinitionNotFound = "DefinitionNotFound";
    public const string MemberNotFound = "MemberNotFound";
    public const string ProjectNotFound = "ProjectNotFound";
    public const string CompilationUnavailable = "CompilationUnavailable";
    public const string SoftBudgetExceeded = "SoftBudgetExceeded";
    public const string GeneratorNotFound = "GeneratorNotFound";
    public const string GeneratedSymbolRenameRefused = "GeneratedSymbolRenameRefused";
    public const string RenameLanguageNotSupported = "RenameLanguageNotSupported";
    public const string InvalidRenameName = "InvalidRenameName";
    public const string DiagnosticNotFound = "DiagnosticNotFound";
    public const string DiagnosticAmbiguous = "DiagnosticAmbiguous";
    public const string FixLanguageNotSupported = "FixLanguageNotSupported";
    public const string FixIndexOutOfRange = "FixIndexOutOfRange";
    public const string GeneratedDocumentFixRefused = "GeneratedDocumentFixRefused";
    public const string FixApplyFailed = "FixApplyFailed";
    public const string FixAllUnavailable = "FixAllUnavailable";
    public const string FixAllBudgetExceeded = "FixAllBudgetExceeded";
    public const string RefactoringLanguageNotSupported = "RefactoringLanguageNotSupported";
    public const string RefactoringIndexOutOfRange = "RefactoringIndexOutOfRange";
    public const string GeneratedSymbolRefactoringRefused = "GeneratedSymbolRefactoringRefused";
    public const string GeneratedDocumentRefactoringRefused = "GeneratedDocumentRefactoringRefused";
    public const string RefactoringApplyFailed = "RefactoringApplyFailed";
}

public static class DeclarationAvailability
{
    public const string InSource = "InSource";
    public const string InMetadata = "InMetadata";
    public const string None = "None";
}

public static class SymbolOrigin
{
    public const string Handwritten = "Handwritten";
    public const string SourceGenerator = "SourceGenerator";

    /// <summary>Parseable Origin label: SourceGenerator(Assembly::Type@Version).</summary>
    public static string FormatSourceGenerator(GeneratorIdentity identity) =>
        $"{SourceGenerator}({identity.AssemblyName}::{identity.TypeFullName}@{identity.Version})";
}

