using Microsoft.CodeAnalysis;

namespace DotNetMcp.Core;

public sealed class SymbolQueryService
{
    public const string CSharpLanguage = "csharp";
    public const string VbLanguage = "vb";
    public const string FSharpLanguage = "fsharp";
    public const int DefaultMemberPageLimit = 50;
    public const int MaxMemberPageLimit = 100;
    public static readonly TimeSpan DependencyClosureSoftBudget =
        SoftBudgetOptions.Default.FindRefsScoped;
    public static readonly TimeSpan EntireSolutionSoftBudget =
        SoftBudgetOptions.Default.FindRefsEntireSolution;

    private readonly LanguageAdapters _languages;
    private readonly RoslynLanguageAdapter _roslyn;

    public SymbolQueryService(
        GeneratorQueryService generators,
        SoftBudgetOptions? softBudgets = null,
        ILanguageAdapter? extraAdapter = null)
        : this(Build(generators, softBudgets, extraAdapter))
    {
    }

    public SymbolQueryService(LanguageAdapters languages, RoslynLanguageAdapter roslyn)
    {
        _languages = languages;
        _roslyn = roslyn;
    }

    private SymbolQueryService((LanguageAdapters Languages, RoslynLanguageAdapter Roslyn) state)
        : this(state.Languages, state.Roslyn)
    {
    }

    internal LanguageAdapters Languages => _languages;

    public Task<(SymbolResolveSuccess? Success, SymbolQueryError? Error)> ResolveByNameAsync(
        IWorkspaceSession session,
        string name,
        string? projectId = null,
        CancellationToken cancellationToken = default) =>
        _languages.ResolveByNameAsync(session, name, projectId, cancellationToken);

    public Task<(SymbolResolveSuccess? Success, SymbolQueryError? Error)> GetSummaryAsync(
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken = default) =>
        Dispatch(handle, adapter => adapter.GetSummaryAsync(session, handle, cancellationToken));

    public Task<(SymbolDefinitionSuccess? Success, SymbolQueryError? Error)> GetDefinitionAsync(
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken = default) =>
        Dispatch(handle, adapter => adapter.GetDefinitionAsync(session, handle, cancellationToken));

    public Task<(PagedResult<MemberListItem>? Success, SymbolQueryError? Error)> GetMembersAsync(
        IWorkspaceSession session,
        string handle,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default) =>
        Dispatch(handle, adapter => adapter.GetMembersAsync(session, handle, limit, cursor, cancellationToken));

    public Task<(PagedResult<ImplementationItem>? Success, SymbolQueryError? Error)> FindImplementationsAsync(
        IWorkspaceSession session,
        string handle,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default) =>
        Dispatch(handle, adapter => adapter.FindImplementationsAsync(session, handle, limit, cursor, cancellationToken));

    public Task<(PagedResult<HierarchyItem>? Success, SymbolQueryError? Error)> GetTypeHierarchyAsync(
        IWorkspaceSession session,
        string handle,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default) =>
        Dispatch(handle, adapter => adapter.GetTypeHierarchyAsync(session, handle, limit, cursor, cancellationToken));

    public Task<(PagedResult<CallerLocationItem>? Success, SymbolQueryError? Error)> FindCallersAsync(
        IWorkspaceSession session,
        string handle,
        int? limit = null,
        string? cursor = null,
        TimeSpan? softBudget = null,
        CancellationToken cancellationToken = default) =>
        Dispatch(handle, adapter => adapter.FindCallersAsync(session, handle, limit, cursor, softBudget, cancellationToken));

    public Task<(PagedResult<ReferenceLocationItem>? Success, SymbolQueryError? Error)> FindReferencesAsync(
        IWorkspaceSession session,
        string handle,
        bool entireSolution = false,
        int? limit = null,
        string? cursor = null,
        TimeSpan? softBudget = null,
        CancellationToken cancellationToken = default) =>
        Dispatch(
            handle,
            adapter => adapter.FindReferencesAsync(
                session, handle, entireSolution, limit, cursor, softBudget, cancellationToken));

    public Task<(SymbolAttributionSuccess? Success, SymbolQueryError? Error)> GetAttributionAsync(
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken = default) =>
        _roslyn.GetAttributionAsync(session, handle, cancellationToken);

    internal Task<(TypeMemberLookup? Success, SymbolQueryError? Error)> LookupTypeMemberAsync(
        IWorkspaceSession session,
        string typeHandle,
        string memberName,
        CancellationToken cancellationToken = default) =>
        _roslyn.LookupTypeMemberAsync(session, typeHandle, memberName, cancellationToken);

    internal Task<(TypeMemberLookup? Success, SymbolQueryError? Error)> LookupTypeMemberAsync(
        IWorkspaceSession session,
        string typeHandle,
        string memberName,
        bool publicOnly,
        CancellationToken cancellationToken = default) =>
        _roslyn.LookupTypeMemberAsync(session, typeHandle, memberName, publicOnly, cancellationToken);

    internal (TypeMemberLookup? Success, SymbolQueryError? Error) LookupTypeMember(
        Project project,
        ITypeSymbol type,
        string memberName,
        bool publicOnly = true) =>
        _roslyn.LookupTypeMember(project, type, memberName, publicOnly);

    internal string FormatHandle(Project project, ISymbol symbol) =>
        _roslyn.FormatHandle(project, symbol);

    internal Task<(Project? Project, ISymbol? Symbol, SymbolQueryError? Error)> ResolveHandleSymbolAsync(
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken = default) =>
        _roslyn.ResolveHandleSymbolAsync(session, handle, cancellationToken);

    public Task<(Project? Project, ISymbol? Symbol, SymbolQueryError? Error)> ResolveHandleAsync(
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken = default) =>
        _roslyn.ResolveHandleAsync(session, handle, cancellationToken);

    internal static string DetectInteropKind(ISymbol symbol) =>
        RoslynLanguageAdapter.DetectInteropKind(symbol);

    public static string LanguageToken(string roslynLanguage) =>
        RoslynLanguageAdapter.LanguageToken(roslynLanguage);

    public static bool IsSupportedLanguageToken(string token) =>
        string.Equals(token, CSharpLanguage, StringComparison.Ordinal) ||
        string.Equals(token, VbLanguage, StringComparison.Ordinal);

    public static bool IsSupportedRoslynLanguage(string roslynLanguage) =>
        roslynLanguage is LanguageNames.CSharp or LanguageNames.VisualBasic;

    private Task<(T? Success, SymbolQueryError? Error)> Dispatch<T>(
        string handle,
        Func<ILanguageAdapter, Task<(T? Success, SymbolQueryError? Error)>> action)
        where T : class
    {
        if (!_languages.TryGetForHandle(handle, out var adapter, out var error))
        {
            return Task.FromResult<(T? Success, SymbolQueryError? Error)>((null, error));
        }

        return action(adapter);
    }

    private static (LanguageAdapters Languages, RoslynLanguageAdapter Roslyn) Build(
        GeneratorQueryService generators,
        SoftBudgetOptions? softBudgets,
        ILanguageAdapter? extraAdapter)
    {
        var roslyn = extraAdapter as RoslynLanguageAdapter
            ?? new RoslynLanguageAdapter(generators, softBudgets);
        ILanguageAdapter[] adapters = extraAdapter is null || ReferenceEquals(extraAdapter, roslyn)
            ? [roslyn]
            : [roslyn, extraAdapter];
        return (new LanguageAdapters(adapters), roslyn);
    }
}
