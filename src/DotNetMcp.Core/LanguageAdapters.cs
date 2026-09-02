using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Core;

/// <summary>
/// DTO facade (ADR-0001 §3): selects an <see cref="ILanguageAdapter"/> once from
/// <see cref="SymbolHandle.Language"/> or project language, then dispatches.
/// Core query modules must not copy language <c>if</c>s or re-declare this hop.
/// </summary>
public sealed class LanguageAdapters
{
    public const string CSharpLanguage = "csharp";
    public const string VbLanguage = "vb";
    public const string FSharpLanguage = "fsharp";
    public const int DefaultMemberPageLimit = 50;
    public const int MaxMemberPageLimit = 100;

    private readonly IReadOnlyList<ILanguageAdapter> _adapters;

    public LanguageAdapters(IEnumerable<ILanguageAdapter> adapters)
    {
        _adapters = adapters.ToArray();
    }

    public IReadOnlyList<ILanguageAdapter> All => _adapters;

    public bool TryGet(string languageToken, [NotNullWhen(true)] out ILanguageAdapter? adapter)
    {
        adapter = _adapters.FirstOrDefault(a => a.OwnsLanguage(languageToken));
        return adapter is not null;
    }

    public ILanguageAdapter? ForProject(Project project) =>
        _adapters.FirstOrDefault(a => a.OwnsProject(project));

    public ILanguageAdapter? ForProjectId(Solution solution, string projectId)
    {
        var project = solution.Projects.FirstOrDefault(p =>
            string.Equals(p.Id.Id.ToString("D"), projectId, StringComparison.OrdinalIgnoreCase));
        return project is null ? null : ForProject(project);
    }

    public ILanguageAdapter? ForProjectId(IWorkspaceSession session, string projectId)
    {
        var fromSolution = ForProjectId(session.Solution, projectId);
        if (fromSolution is not null)
        {
            return fromSolution;
        }

        if (session.FSharpSnapshot.FindProject(projectId) is null)
        {
            return null;
        }

        return TryGet(FSharpLanguage, out var adapter) ? adapter : null;
    }

    public bool TryGetForHandle(
        string handle,
        [NotNullWhen(true)] out ILanguageAdapter? adapter,
        [NotNullWhen(false)] out SymbolQueryError? error)
    {
        adapter = null;
        if (!SymbolHandle.TryParse(handle, out var parsed, out var parseError) || parsed is null)
        {
            error = new InvalidSymbolHandleError(
                parseError ?? "Handle format or checksum is invalid.",
                "Call symbol_resolve with a name/FQN to obtain a fresh SymbolHandle; do not invent handles.");
            return false;
        }

        if (!TryGet(parsed.Language, out adapter))
        {
            error = new InvalidSymbolHandleError(
                $"Unsupported language '{parsed.Language}'.",
                "Call symbol_resolve for a supported language to obtain a fresh SymbolHandle.");
            return false;
        }

        error = null;
        return true;
    }

    public async Task<(SymbolResolveSuccess? Success, SymbolQueryError? Error)> ResolveByNameAsync(
        IWorkspaceSession session,
        string name,
        string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, new SymbolNotFoundError(
                "Symbol name is empty.",
                "Pass a type or member name / FQN to symbol_resolve."));
        }

        if (!string.IsNullOrWhiteSpace(projectId))
        {
            var adapter = ForProjectId(session, projectId);
            if (adapter is null)
            {
                return (null, new SymbolNotFoundError(
                    $"No project with projectId '{projectId}' is in the ready workspace.",
                    "Call workspace_list_projects for valid projectId values, then retry symbol_resolve."));
            }

            return await adapter
                .ResolveByNameAsync(session, name, projectId, cancellationToken)
                .ConfigureAwait(false);
        }

        ILanguageAdapter? primary = null;
        var fallbacks = new List<ILanguageAdapter>();
        foreach (var adapter in _adapters)
        {
            if (primary is null &&
                (adapter.OwnsLanguage(CSharpLanguage) || adapter.OwnsLanguage(VbLanguage)))
            {
                primary = adapter;
            }
            else
            {
                fallbacks.Add(adapter);
            }
        }

        if (primary is not null)
        {
            var roslyn = await primary
                .ResolveByNameAsync(session, name, projectId: null, cancellationToken)
                .ConfigureAwait(false);
            if (roslyn.Error is SymbolAmbiguousError)
            {
                return roslyn;
            }

            if (roslyn.Success is not null &&
                await IsSourceDefiningAsync(primary, session, roslyn.Success.Handle, cancellationToken)
                    .ConfigureAwait(false))
            {
                return roslyn;
            }

            foreach (var fallback in fallbacks)
            {
                var hit = await fallback
                    .ResolveByNameAsync(session, name, projectId: null, cancellationToken)
                    .ConfigureAwait(false);
                if (hit.Success is not null)
                {
                    return hit;
                }
            }

            if (roslyn.Success is not null || roslyn.Error is not null)
            {
                return roslyn;
            }
        }
        else
        {
            foreach (var fallback in fallbacks)
            {
                var hit = await fallback
                    .ResolveByNameAsync(session, name, projectId: null, cancellationToken)
                    .ConfigureAwait(false);
                if (hit.Success is not null || hit.Error is not SymbolNotFoundError)
                {
                    return hit;
                }
            }
        }

        return (null, new SymbolNotFoundError(
            $"No symbol named '{name}' was found in the ready workspace.",
            "Confirm the name/FQN (and optional projectId), then call symbol_resolve again."));
    }

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

    public Task<(SymbolAttributionSuccess? Success, SymbolQueryError? Error)> GetAttributionAsync(
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken = default) =>
        Dispatch(handle, adapter => adapter.GetAttributionAsync(session, handle, cancellationToken));

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

    public async Task<(RenamePreviewDraft? Draft, SymbolQueryError? Error)> BuildRenamePreviewAsync(
        IWorkspaceSession session,
        string handle,
        string newName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(['.', ' ', '\t']) >= 0)
        {
            return (null, new InvalidRenameNameError(
                "New name must be a single identifier.",
                "Pass a C# identifier (no qualification) as newName."));
        }

        if (!TryGetForHandle(handle, out var adapter, out var error))
        {
            return (null, error);
        }

        return await adapter
            .BuildRenamePreviewAsync(session, handle, newName, cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<(T? Success, SymbolQueryError? Error)> Dispatch<T>(
        string handle,
        Func<ILanguageAdapter, Task<(T? Success, SymbolQueryError? Error)>> action)
        where T : class
    {
        if (!TryGetForHandle(handle, out var adapter, out var error))
        {
            return Task.FromResult<(T? Success, SymbolQueryError? Error)>((null, error));
        }

        return action(adapter);
    }

    private static async Task<bool> IsSourceDefiningAsync(
        ILanguageAdapter adapter,
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken)
    {
        var (definition, _) = await adapter
            .GetDefinitionAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        return definition?.Locations.Any(static l =>
            string.Equals(l.DeclarationAvailability, DeclarationAvailability.InSource, StringComparison.Ordinal))
            == true;
    }
}
