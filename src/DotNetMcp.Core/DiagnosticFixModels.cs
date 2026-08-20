namespace DotNetMcp.Core;

public sealed record DiagnosticFixItem(
    int FixIndex,
    string Title,
    string? EquivalenceKey);

public sealed record DiagnosticFixListSuccess(IReadOnlyList<DiagnosticFixItem> Items);

public sealed record DiagnosticFixPreviewDraft(
    string Title,
    string? EquivalenceKey,
    string Scope,
    IReadOnlyList<RenameDocumentSlice> Documents,
    IReadOnlyList<string> InvalidatedHandles);

public sealed record DiagnosticNotFoundError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.DiagnosticNotFound, Message, SuggestedAction);

public sealed record DiagnosticAmbiguousError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.DiagnosticAmbiguous, Message, SuggestedAction);

public sealed record FixLanguageNotSupportedError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.FixLanguageNotSupported, Message, SuggestedAction);

public sealed record FixIndexOutOfRangeError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.FixIndexOutOfRange, Message, SuggestedAction);

public sealed record GeneratedDocumentFixRefusedError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.GeneratedDocumentFixRefused, Message, SuggestedAction);

public sealed record FixApplyFailedError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.FixApplyFailed, Message, SuggestedAction);

public sealed record FixAllUnavailableError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.FixAllUnavailable, Message, SuggestedAction);

public sealed record FixAllBudgetExceededError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.FixAllBudgetExceeded, Message, SuggestedAction);

public static class DiagnosticFixScopes
{
    public const string Occurrence = "occurrence";
    public const string Document = "document";
    public const string Project = "project";
}
