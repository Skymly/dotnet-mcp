using System.Diagnostics;
using DotNetMcp.Core;
using FSharp.Compiler.CodeAnalysis;
using FSharp.Compiler.Symbols;
using Microsoft.FSharp.Core;
using FcsRange = global::FSharp.Compiler.Text.Range;

namespace DotNetMcp.FSharp;

public sealed partial class FSharpSymbolQueryService
{
    public async Task<(PagedResult<ReferenceLocationItem>? Success, SymbolQueryError? Error)> FindReferencesAsync(
        IWorkspaceSession session,
        string handle,
        bool entireSolution = false,
        int? limit = null,
        string? cursor = null,
        TimeSpan? softBudget = null,
        CancellationToken cancellationToken = default)
    {
        var (item, _, check, error) = await TryResolveWithCheckAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        var pageLimit = limit is null or < 1
            ? LanguageAdapters.DefaultMemberPageLimit
            : Math.Min(limit.Value, LanguageAdapters.MaxMemberPageLimit);
        var budget = softBudget ?? (entireSolution
            ? _softBudgets.FindRefsEntireSolution
            : _softBudgets.FindRefsScoped);
        var clock = Stopwatch.StartNew();

        var hits = new List<ReferenceLocationItem>();
        var truncatedByBudget = false;
        if (check is not null)
        {
            foreach (var use in check.GetAllUsesOfAllSymbols(null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clock.Elapsed >= budget)
                {
                    truncatedByBudget = true;
                    break;
                }

                if (!SameSymbol(use.Symbol, item!))
                {
                    continue;
                }

                hits.Add(ToReference(
                    item!.ProjectId,
                    use.FileName,
                    use.Range,
                    use.IsFromDefinition
                        ? ReferenceLocationKind.Definition
                        : ReferenceLocationKind.Reference));
            }
        }

        return Page(hits, session.Epoch, entireSolution, pageLimit, cursor, "symbol_find_references", handle, truncatedByBudget);
    }

    public async Task<(PagedResult<ImplementationItem>? Success, SymbolQueryError? Error)> FindImplementationsAsync(
        IWorkspaceSession session,
        string handle,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var (item, project, _, error) = await TryResolveWithCheckAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        if (!item!.IsContainer)
        {
            return (null, new SymbolNotFoundError(
                "Handle does not refer to a named type; implementations require a type SymbolHandle.",
                "Call symbol_resolve for a type name/FQN, then call symbol_find_implementations with that handle."));
        }

        var catalog = await CatalogAsync(project!, cancellationToken).ConfigureAwait(false);
        var impls = catalog
            .Where(candidate => candidate.IsContainer && candidate.SignatureQualifiedName != item.SignatureQualifiedName)
            .Where(candidate =>
                item.IsInterface
                    ? (candidate.InterfaceNames ?? []).Contains(item.SignatureQualifiedName, StringComparer.Ordinal)
                    : InheritsFrom(candidate, item.SignatureQualifiedName, catalog))
            .Select(candidate =>
            {
                var success = ToSuccess(candidate);
                return new ImplementationItem(success.Handle, success.Summary, candidate.Locations);
            })
            .ToList();

        return Page(impls, session.Epoch, pageLimit: limit, cursor, "symbol_find_implementations", handle,
            emptyMessage: "No implementations were found.");
    }

    public async Task<(PagedResult<HierarchyItem>? Success, SymbolQueryError? Error)> GetTypeHierarchyAsync(
        IWorkspaceSession session,
        string handle,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var (item, project, _, error) = await TryResolveWithCheckAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        if (!item!.IsContainer)
        {
            return (null, new SymbolNotFoundError(
                "Handle does not refer to a named type; type hierarchy requires a type SymbolHandle.",
                "Call symbol_resolve for a type name/FQN, then call symbol_type_hierarchy with that handle."));
        }

        var catalog = await CatalogAsync(project!, cancellationToken).ConfigureAwait(false);
        var byName = catalog.Where(c => c.IsContainer)
            .ToDictionary(c => c.SignatureQualifiedName, StringComparer.Ordinal);
        var chain = new List<HierarchyItem>();

        var current = item.BaseTypeName;
        while (!string.IsNullOrWhiteSpace(current) && byName.TryGetValue(current, out var parent))
        {
            var success = ToSuccess(parent);
            chain.Add(new HierarchyItem(HierarchyRelationKind.BaseType, success.Handle, success.Summary));
            current = parent.BaseTypeName;
        }

        foreach (var ifaceName in item.InterfaceNames ?? [])
        {
            if (!byName.TryGetValue(ifaceName, out var iface))
            {
                continue;
            }

            var success = ToSuccess(iface);
            chain.Add(new HierarchyItem(HierarchyRelationKind.Interface, success.Handle, success.Summary));
        }

        return Page(chain, session.Epoch, pageLimit: limit, cursor, "symbol_type_hierarchy", handle,
            emptyMessage: "Type has no base types or interfaces.");
    }

    public async Task<(PagedResult<CallerLocationItem>? Success, SymbolQueryError? Error)> FindCallersAsync(
        IWorkspaceSession session,
        string handle,
        bool entireSolution = false,
        int? limit = null,
        string? cursor = null,
        TimeSpan? softBudget = null,
        CancellationToken cancellationToken = default)
    {
        var (item, project, check, error) = await TryResolveWithCheckAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        if (item!.IsContainer)
        {
            return (null, new SymbolNotFoundError(
                "Handle does not refer to a method; callers require a method SymbolHandle.",
                "Call symbol_resolve for a method name/FQN, then call symbol_find_callers with that handle."));
        }

        var budget = softBudget ?? (entireSolution
            ? _softBudgets.FindRefsEntireSolution
            : _softBudgets.FindRefsScoped);
        var clock = Stopwatch.StartNew();
        var hits = new List<CallerLocationItem>();
        var truncatedByBudget = false;
        if (check is not null)
        {
            var catalog = project is null
                ? []
                : FlattenCatalog(await CatalogAsync(project, cancellationToken).ConfigureAwait(false)).ToList();
            var uses = check.GetAllUsesOfAllSymbols(null).ToList();
            var definitions = uses.Where(static u => u.IsFromDefinition).ToList();

            foreach (var use in uses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clock.Elapsed >= budget)
                {
                    truncatedByBudget = true;
                    break;
                }

                if (use.IsFromDefinition || !SameSymbol(use.Symbol, item))
                {
                    continue;
                }

                var enclosing = FindEnclosingDefinition(definitions, use, item);
                var callerItem = enclosing is null ? null : MatchCatalog(catalog, enclosing.Symbol);
                if (callerItem is null || SameSymbol(enclosing!.Symbol, item))
                {
                    continue;
                }

                var success = ToSuccess(callerItem);
                var loc = ToLocation(use.FileName, use.Range);
                hits.Add(new CallerLocationItem(
                    loc.DeclarationAvailability,
                    loc.Origin,
                    loc.FilePath,
                    loc.Start,
                    loc.Length,
                    callerItem.ProjectId,
                    success.Handle,
                    success.Summary));
            }
        }

        return Page(hits, session.Epoch, limit, cursor, "symbol_find_callers", handle, "No callers were found.", truncatedByBudget);
    }

    private async Task<(FSharpCatalogItem? Item, FSharpProjectSnapshot? Project, FSharpCheckProjectResults? Check, SymbolQueryError? Error)>
        TryResolveWithCheckAsync(IWorkspaceSession session, string handle, CancellationToken cancellationToken)
    {
        if (!SymbolHandle.TryParse(handle, out var parsed, out var parseError) || parsed is null)
        {
            return (null, null, null, new InvalidSymbolHandleError(
                parseError ?? "Handle format or checksum is invalid.",
                "Call symbol_resolve with a name/FQN to obtain a fresh SymbolHandle; do not invent handles."));
        }

        if (!string.Equals(parsed.Language, LanguageAdapters.FSharpLanguage, StringComparison.Ordinal))
        {
            return (null, null, null, new InvalidSymbolHandleError(
                $"Unsupported language '{parsed.Language}'.",
                "Call symbol_resolve for an F# symbol to obtain a fsharp handle."));
        }

        var project = session.FSharpSnapshot.FindProject(parsed.ProjectId);
        if (project is null)
        {
            return (null, null, null, new SymbolNotFoundError(
                $"No F# project '{parsed.ProjectId}' is in the ready workspace.",
                "Call workspace_list_projects, then symbol_resolve for an F# symbol."));
        }

        var (catalog, check, _) = await CheckProjectAsync(project, cancellationToken).ConfigureAwait(false);
        catalog = FlattenCatalog(catalog).ToList();
        var hit = catalog.FirstOrDefault(item =>
            string.Equals(item.SignatureQualifiedName, parsed.SignatureQualifiedName, StringComparison.Ordinal) ||
            string.Equals(item.DisplayName, parsed.SignatureQualifiedName, StringComparison.Ordinal) ||
            item.SignatureQualifiedName.EndsWith("." + parsed.SignatureQualifiedName, StringComparison.Ordinal));
        if (hit is null)
        {
            return (null, project, check, new SymbolNotFoundError(
                $"Symbol '{parsed.SignatureQualifiedName}' was not found in project '{parsed.ProjectId}'.",
                "Call symbol_resolve with a name/FQN to obtain a fresh SymbolHandle."));
        }

        return (hit, project, check, null);
    }

    private static bool SameSymbol(FSharpSymbol symbol, FSharpCatalogItem item)
    {
        var name = symbol.FullName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = symbol.DisplayName;
        }

        if (string.Equals(name, item.SignatureQualifiedName, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(symbol.DisplayName, item.DisplayName, StringComparison.Ordinal))
        {
            return false;
        }

        if (!OptionModule.IsSome(symbol.DeclarationLocation))
        {
            return false;
        }

        var declaredFile = symbol.DeclarationLocation.Value.FileName;
        return item.Locations.Any(location => SameDocumentPath(location.FilePath, declaredFile));
    }

    private static bool InheritsFrom(FSharpCatalogItem candidate, string baseName, IReadOnlyList<FSharpCatalogItem> catalog)
    {
        var current = candidate.BaseTypeName;
        var guard = 0;
        while (!string.IsNullOrWhiteSpace(current) && guard++ < 16)
        {
            if (string.Equals(current, baseName, StringComparison.Ordinal))
            {
                return true;
            }

            current = catalog.FirstOrDefault(c => c.SignatureQualifiedName == current)?.BaseTypeName;
        }

        return false;
    }


    private static FSharpSymbolUse? FindEnclosingDefinition(
        IReadOnlyList<FSharpSymbolUse> definitions,
        FSharpSymbolUse call,
        FSharpCatalogItem callee)
    {
        FSharpSymbolUse? best = null;
        foreach (var definition in definitions)
        {
            if (SameSymbol(definition.Symbol, callee) ||
                !string.Equals(definition.FileName, call.FileName, StringComparison.OrdinalIgnoreCase) ||
                !RangeStartsOnOrBefore(definition.Range, call.Range))
            {
                continue;
            }

            if (best is null || RangeStartsOnOrBefore(best.Range, definition.Range))
            {
                best = definition;
            }
        }

        return best;
    }

    private static bool RangeStartsOnOrBefore(FcsRange left, FcsRange right) =>
        left.StartLine < right.StartLine ||
        (left.StartLine == right.StartLine && left.StartColumn <= right.StartColumn);

    private static FSharpCatalogItem? MatchCatalog(IReadOnlyList<FSharpCatalogItem> catalog, FSharpSymbol symbol)
    {
        var hits = catalog.Where(item => SameSymbol(symbol, item)).ToList();
        return hits.FirstOrDefault(static item => !item.IsContainer) ?? hits.FirstOrDefault();
    }

    private static bool RangeContains(FcsRange outer, FcsRange inner)
    {
        var startsBeforeOrAt =
            outer.StartLine < inner.StartLine ||
            (outer.StartLine == inner.StartLine && outer.StartColumn <= inner.StartColumn);
        var endsAfterOrAt =
            outer.EndLine > inner.EndLine ||
            (outer.EndLine == inner.EndLine && outer.EndColumn >= inner.EndColumn);
        return startsBeforeOrAt && endsAfterOrAt;
    }

    private static bool RangeEquals(FcsRange left, FcsRange right) =>
        left.StartLine == right.StartLine &&
        left.StartColumn == right.StartColumn &&
        left.EndLine == right.EndLine &&
        left.EndColumn == right.EndColumn;

    private static int RangeSize(FcsRange range) =>
        ((range.EndLine - range.StartLine) * 1_000_000) + (range.EndColumn - range.StartColumn);

    private ReferenceLocationItem ToReference(string projectId, string file, FcsRange range, string kind)
    {
        var loc = ToLocation(file, range);
        return new ReferenceLocationItem(
            loc.DeclarationAvailability,
            loc.Origin,
            loc.FilePath,
            loc.Start,
            loc.Length,
            projectId,
            kind);
    }

    private SymbolLocation ToLocation(string file, FcsRange range)
    {
        if (TryGetSnapshot(file, out var path, out var text))
        {
            var (start, length) = ToSpan(text, range);
            return new SymbolLocation(DeclarationAvailability.InSource, SymbolOrigin.Handwritten, path, start, length);
        }

        return new SymbolLocation(
            DeclarationAvailability.InSource,
            SymbolOrigin.Handwritten,
            string.IsNullOrWhiteSpace(file) ? null : file,
            null,
            null);
    }

    private static (PagedResult<T>? Success, SymbolQueryError? Error) Page<T>(
        IReadOnlyList<T> items,
        long epoch,
        int? pageLimit,
        string? cursor,
        string tool,
        string queryId,
        string emptyMessage,
        bool truncatedByBudget = false)
    {
        var limit = pageLimit is null or < 1
            ? LanguageAdapters.DefaultMemberPageLimit
            : Math.Min(pageLimit.Value, LanguageAdapters.MaxMemberPageLimit);
        return SoftBudgetPage.Page(
            items,
            epoch,
            truncatedByBudget,
            cursor,
            limit,
            tool,
            queryId,
            emptyMessage,
            "Page complete.",
            scanIncomplete: truncatedByBudget);
    }

    private static (PagedResult<ReferenceLocationItem>? Success, SymbolQueryError? Error) Page(
        IReadOnlyList<ReferenceLocationItem> items,
        long epoch,
        bool entireSolution,
        int pageLimit,
        string? cursor,
        string tool,
        string queryId,
        bool truncatedByBudget = false)
    {
        return SoftBudgetPage.PageFindRefs(
            items,
            epoch,
            entireSolution,
            truncatedByBudget,
            cursor,
            pageLimit,
            tool,
            queryId,
            "No references were found.",
            "Page complete.",
            scanIncomplete: truncatedByBudget);
    }
}
