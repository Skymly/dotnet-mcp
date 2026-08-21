using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Core;

/// <summary>
/// Selects an <see cref="ILanguageAdapter"/> once from <see cref="SymbolHandle.Language"/>
/// or project language. Core query modules must not copy language <c>if</c>s.
/// </summary>
public sealed class LanguageAdapters
{
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
            var adapter = ForProjectId(session.Solution, projectId);
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
                (adapter.OwnsLanguage(SymbolQueryService.CSharpLanguage) ||
                 adapter.OwnsLanguage(SymbolQueryService.VbLanguage)))
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
