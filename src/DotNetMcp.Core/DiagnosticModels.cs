namespace DotNetMcp.Core;

public sealed record DiagnosticItem(
    string Id,
    string Severity,
    string Message,
    string? FilePath,
    int? StartLine,
    int? StartCharacter,
    int? EndLine,
    int? EndCharacter,
    string ProjectId);

public sealed record ProjectNotFoundError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.ProjectNotFound, Message, SuggestedAction);

public sealed record CompilationUnavailableError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.CompilationUnavailable, Message, SuggestedAction);

public sealed record SoftBudgetExceededError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.SoftBudgetExceeded, Message, SuggestedAction);
