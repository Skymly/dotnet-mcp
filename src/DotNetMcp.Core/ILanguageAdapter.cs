using Microsoft.CodeAnalysis;

namespace DotNetMcp.Core;

/// <summary>
/// Language seam (ADR-0001 §5). Roslyn (C#/VB) and FCS (F#) are the two adapters.
/// XAML is a caller of Core inner APIs, not an adapter.
/// </summary>
public interface ILanguageAdapter
{
    bool OwnsLanguage(string languageToken);

    bool OwnsProject(Project project);

    bool SupportsCodeRefactoring { get; }

    bool SupportsDiagnosticFix { get; }

    bool SupportsSourceGenerators { get; }

    bool SupportsDynamicInvocations { get; }

    Task<(SymbolResolveSuccess? Success, SymbolQueryError? Error)> ResolveByNameAsync(
        IWorkspaceSession session,
        string name,
        string? projectId = null,
        CancellationToken cancellationToken = default);

    Task<(SymbolResolveSuccess? Success, SymbolQueryError? Error)> GetSummaryAsync(
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Position entry (ADR-0001 Amendment 7): resolves the symbol at a
    /// <paramref name="line"/>/<paramref name="column"/> position inside a
    /// workspace document to the same addressable set as
    /// <see cref="ResolveByNameAsync"/>. Positions use the unified model
    /// (1-based, UTF-16 code units); the adapter clamps columns past the line
    /// end and may also match the identifier immediately left of the position.
    /// The adapter owns its own document enumeration (regular + generated
    /// documents for Roslyn; the F# snapshot for FCS).
    /// </summary>
    Task<(SymbolResolveAtSuccess? Success, SymbolQueryError? Error)> ResolveAtPositionAsync(
        IWorkspaceSession session,
        string path,
        int line,
        int column,
        string? projectId = null,
        CancellationToken cancellationToken = default);

    Task<(SymbolDefinitionSuccess? Success, SymbolQueryError? Error)> GetDefinitionAsync(
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken = default);

    Task<(SymbolAttributionSuccess? Success, SymbolQueryError? Error)> GetAttributionAsync(
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken = default);

    Task<(PagedResult<MemberListItem>? Success, SymbolQueryError? Error)> GetMembersAsync(
        IWorkspaceSession session,
        string handle,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default);

    Task<(PagedResult<ReferenceLocationItem>? Success, SymbolQueryError? Error)> FindReferencesAsync(
        IWorkspaceSession session,
        string handle,
        bool entireSolution = false,
        int? limit = null,
        string? cursor = null,
        TimeSpan? softBudget = null,
        CancellationToken cancellationToken = default);

    Task<(PagedResult<ImplementationItem>? Success, SymbolQueryError? Error)> FindImplementationsAsync(
        IWorkspaceSession session,
        string handle,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default);

    Task<(PagedResult<HierarchyItem>? Success, SymbolQueryError? Error)> GetTypeHierarchyAsync(
        IWorkspaceSession session,
        string handle,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default);

    Task<(PagedResult<CallerLocationItem>? Success, SymbolQueryError? Error)> FindCallersAsync(
        IWorkspaceSession session,
        string handle,
        bool entireSolution = false,
        int? limit = null,
        string? cursor = null,
        TimeSpan? softBudget = null,
        CancellationToken cancellationToken = default);

    Task<(SymbolSourceSuccess? Success, SymbolQueryError? Error)> GetSourceAsync(
        IWorkspaceSession session,
        string handle,
        int maxChars,
        CancellationToken cancellationToken = default);

    Task<(PagedResult<DiagnosticItem>? Success, SymbolQueryError? Error)> GetProjectDiagnosticsAsync(
        IWorkspaceSession session,
        string projectId,
        int? limit = null,
        string? cursor = null,
        TimeSpan? softBudget = null,
        CancellationToken cancellationToken = default);

    Task<(RenamePreviewDraft? Draft, SymbolQueryError? Error)> BuildRenamePreviewAsync(
        IWorkspaceSession session,
        string handle,
        string newName,
        CancellationToken cancellationToken = default);
}
