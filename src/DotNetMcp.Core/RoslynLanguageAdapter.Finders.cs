using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;

namespace DotNetMcp.Core;

public sealed partial class RoslynLanguageAdapter
{
    public async Task<(PagedResult<ImplementationItem>? Success, SymbolQueryError? Error)> FindImplementationsAsync(
        IWorkspaceSession session,
        string handle,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {

        var epoch = session.Epoch;
        var (project, symbol, error) = await TryResolveHandleAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        var pageLimit = ClampLimit(limit);
        if (!SoftBudgetPage.TryReadOffset(
                cursor,
                epoch,
                "symbol_find_implementations",
                out var offset,
                out var cursorError))
        {
            return (null, cursorError);
        }

        var found = new List<ISymbol>();
        var implementations = await SymbolFinder
            .FindImplementationsAsync(symbol!, session.Solution, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        found.AddRange(implementations);

        if (symbol is INamedTypeSymbol named)
        {
            if (named.TypeKind is TypeKind.Class or TypeKind.Struct)
            {
                var derived = await SymbolFinder
                    .FindDerivedClassesAsync(
                        named,
                        session.Solution,
                        transitive: true,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                found.AddRange(derived);
            }

            if (named.TypeKind == TypeKind.Interface)
            {
                var derivedIfaces = await SymbolFinder
                    .FindDerivedInterfacesAsync(
                        named,
                        session.Solution,
                        transitive: true,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                found.AddRange(derivedIfaces);
            }
        }

        var ordered = found
            .Where(s => !SymbolEqualityComparer.Default.Equals(s, symbol))
            .Select(s => (Symbol: s, Project: ProjectForSymbol(session.Solution, s, project!)))
            .DistinctBy(m => (m.Project.Id.Id, SymbolKey(m.Symbol)))
            .OrderBy(m => SymbolKey(m.Symbol), StringComparer.Ordinal)
            .ThenBy(m => m.Project.Id.Id.ToString("D"), StringComparer.Ordinal)
            .ToList();

        if (offset > ordered.Count)
        {
            return (null, SoftBudgetPage.PastEnd("symbol_find_implementations", "the implementation list"));
        }

        var slice = ordered.Skip(offset).Take(pageLimit).ToList();
        var items = new List<ImplementationItem>(slice.Count);
        foreach (var (impl, owner) in slice)
        {
            var (item, mapError) = await ToImplementationItemAsync(session, owner, impl, cancellationToken)
                .ConfigureAwait(false);
            if (mapError is not null)
            {
                return (null, mapError);
            }

            items.Add(item!);
        }

        var nextOffset = offset + items.Count;
        return (SoftBudgetPage.Finish(
            items,
            moreItems: nextOffset < ordered.Count,
            budgetHit: false,
            () => MemberPageCursor.Encode(epoch, nextOffset),
            "symbol_find_implementations",
            ordered.Count == 0
                ? "No implementations or derived types found."
                : "Implementation page complete."), null);
    }

    public async Task<(PagedResult<HierarchyItem>? Success, SymbolQueryError? Error)> GetTypeHierarchyAsync(
        IWorkspaceSession session,
        string handle,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {

        var epoch = session.Epoch;
        var (project, symbol, error) = await TryResolveHandleAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        if (symbol is not INamedTypeSymbol type)
        {
            return (null, new SymbolNotFoundError(
                "Handle does not refer to a named type; type hierarchy requires a type SymbolHandle.",
                "Call symbol_resolve for a type name/FQN, then call symbol_type_hierarchy with that handle."));
        }

        var pageLimit = ClampLimit(limit);
        var chain = new List<HierarchyItem>();
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            var owner = ProjectForSymbol(session.Solution, current, project!);
            var success = ToSuccess(owner, current);
            chain.Add(new HierarchyItem(HierarchyRelationKind.BaseType, success.Handle, success.Summary));
        }

        foreach (var iface in type.AllInterfaces.OrderBy(SymbolKey, StringComparer.Ordinal))
        {
            var owner = ProjectForSymbol(session.Solution, iface, project!);
            var success = ToSuccess(owner, iface);
            chain.Add(new HierarchyItem(HierarchyRelationKind.Interface, success.Handle, success.Summary));
        }

        return SoftBudgetPage.Page(
            chain,
            epoch,
            budgetHit: false,
            cursor,
            pageLimit,
            "symbol_type_hierarchy",
            "Type has no base types or interfaces.",
            "Type hierarchy page complete.",
            "the type hierarchy");
    }

    public async Task<(PagedResult<CallerLocationItem>? Success, SymbolQueryError? Error)> FindCallersAsync(
        IWorkspaceSession session,
        string handle,
        int? limit = null,
        string? cursor = null,
        TimeSpan? softBudget = null,
        CancellationToken cancellationToken = default)
    {
        var solution = session.Solution;
        var (project, symbol, error) = await TryResolveHandleAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        if (symbol is not IMethodSymbol)
        {
            return (null, new SymbolNotFoundError(
                "Handle does not refer to a method; callers require a method SymbolHandle.",
                "Call symbol_resolve for a method name/FQN, then call symbol_find_callers with that handle."));
        }

        var pageLimit = ClampLimit(limit);
        var budget = softBudget ?? _softBudgets.FindRefsScoped;
        if (!SoftBudgetPage.TryReadFindRefs(
                cursor,
                session.Epoch,
                entireSolution: false,
                "symbol_find_callers",
                out var docIndex,
                out var locOffset,
                out var cursorError,
                scopeMismatchMessage: "Cursor payload is invalid."))
        {
            return (null, cursorError);
        }

        var documents = FindRefsScopes.DocumentsForScope(solution, project!, FindRefsScopeKind.DependencyClosure)
            .OrderBy(d => d.Project.Name, StringComparer.Ordinal)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ThenBy(d => d.Id.Id)
            .ToList();

        return await PageFinderHitsAsync<CallerLocationItem>(
                session,
                handle,
                scopeKey: "callers-closure",
                entireSolution: false,
                tool: "symbol_find_callers",
                documents,
                budget,
                pageLimit,
                docIndex,
                locOffset,
                async (scan, ct) =>
                {
                    var callers = await SymbolFinder
                        .FindCallersAsync(symbol!, solution, scan.ToImmutableHashSet(), ct)
                        .ConfigureAwait(false);
                    var aligned = new List<IReadOnlyList<CallerLocationItem>>(scan.Count);
                    foreach (var doc in scan)
                    {
                        aligned.Add(await FlattenCallerHitsForDocumentAsync(session, doc, callers, ct)
                            .ConfigureAwait(false));
                    }

                    return aligned;
                },
                emptyMessage: "No direct callers found.",
                completeMessage: "Caller page complete.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(PagedResult<ReferenceLocationItem>? Success, SymbolQueryError? Error)> FindReferencesAsync(
        IWorkspaceSession session,
        string handle,
        bool entireSolution = false,
        int? limit = null,
        string? cursor = null,
        TimeSpan? softBudget = null,
        CancellationToken cancellationToken = default)
    {
        var solution = session.Solution;
        var (project, symbol, error) = await TryResolveHandleAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        var pageLimit = ClampLimit(limit);
        var budget = softBudget ?? (entireSolution
            ? _softBudgets.FindRefsEntireSolution
            : _softBudgets.FindRefsScoped);
        if (!SoftBudgetPage.TryReadFindRefs(
                cursor,
                session.Epoch,
                entireSolution,
                "symbol_find_references",
                out var docIndex,
                out var locOffset,
                out var cursorError,
                scopeMismatchMessage: "Cursor scope does not match the entireSolution parameter for this request."))
        {
            return (null, cursorError);
        }

        var scope = entireSolution
            ? FindRefsScopeKind.EntireSolution
            : FindRefsScopeKind.DependencyClosure;
        var documents = FindRefsScopes.DocumentsForScope(solution, project!, scope)
            .OrderBy(d => d.Project.Name, StringComparer.Ordinal)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ThenBy(d => d.Id.Id)
            .ToList();

        return await PageFinderHitsAsync<ReferenceLocationItem>(
                session,
                handle,
                scopeKey: entireSolution ? "refs-entire" : "refs-closure",
                entireSolution,
                tool: "symbol_find_references",
                documents,
                budget,
                pageLimit,
                docIndex,
                locOffset,
                async (scan, ct) =>
                {
                    var referenced = await FindRefsScopes
                        .FindReferencesInDocumentsAsync(symbol!, solution, scan.ToImmutableHashSet(), ct)
                        .ConfigureAwait(false);
                    var aligned = new List<IReadOnlyList<ReferenceLocationItem>>(scan.Count);
                    foreach (var doc in scan)
                    {
                        aligned.Add(await FlattenReferenceHitsForDocumentAsync(session, doc, referenced, ct)
                            .ConfigureAwait(false));
                    }

                    return aligned;
                },
                emptyMessage: "No references found in the selected scope.",
                completeMessage: "Reference page complete.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(PagedResult<T>? Success, SymbolQueryError? Error)> PageFinderHitsAsync<T>(
        IWorkspaceSession session,
        string handle,
        string scopeKey,
        bool entireSolution,
        string tool,
        IReadOnlyList<Document> documents,
        TimeSpan budget,
        int pageLimit,
        int docIndex,
        int locOffset,
        Func<IReadOnlyList<Document>, CancellationToken, Task<IReadOnlyList<IReadOnlyList<T>>>> findAligned,
        string emptyMessage,
        string completeMessage,
        CancellationToken cancellationToken)
    {
        if (docIndex > documents.Count || (docIndex == documents.Count && locOffset > 0))
        {
            return (null, new StaleCursorError(
                "Cursor document index is past the end of the scoped document list.",
                $"Call {tool} again without a cursor to start a fresh page."));
        }

        var cache = session as IWorkspaceSessionCaches;
        IReadOnlyList<IReadOnlyList<T>>? byDocument = null;
        var fromCache = cache?.FindHits.TryGetByDocument(session.Epoch, handle, scopeKey, out byDocument) == true;
        var truncatedByBudget = false;

        if (!fromCache)
        {
            if (budget <= TimeSpan.Zero)
            {
                var filled = new IReadOnlyList<T>[documents.Count];
                for (var i = 0; i < documents.Count; i++)
                {
                    filled[i] = Array.Empty<T>();
                }

                for (var i = docIndex; i < documents.Count; i++)
                {
                    var one = await findAligned([documents[i]], cancellationToken).ConfigureAwait(false);
                    filled[i] = one.Count > 0 ? one[0] : Array.Empty<T>();
                    if (filled[i].Count > 0)
                    {
                        break;
                    }
                }

                byDocument = filled;
                truncatedByBudget = true;
            }
            else
            {
                IReadOnlyList<IReadOnlyList<T>> aligned;
                using (var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    budgetCts.CancelAfter(budget);
                    try
                    {
                        aligned = documents.Count == 0
                            ? Array.Empty<IReadOnlyList<T>>()
                            : await findAligned(documents, budgetCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        truncatedByBudget = true;
                        aligned = Array.Empty<IReadOnlyList<T>>();
                    }
                }

                var filled = AlignByDocument(documents, documents, aligned);
                byDocument = filled;
                if (!truncatedByBudget)
                {
                    cache?.FindHits.SetByDocument(session.Epoch, handle, scopeKey, filled);
                }
            }
        }

        if (docIndex < documents.Count && locOffset > byDocument![docIndex].Count)
        {
            return (null, new StaleCursorError(
                "Cursor location offset is past the end of hits for a document.",
                $"Call {tool} again without a cursor to start a fresh page."));
        }

        var (page, exhausted, nextDoc, nextLoc) = SliceByDocument(byDocument!, docIndex, locOffset, pageLimit);
        if (budget <= TimeSpan.Zero && !exhausted)
        {
            truncatedByBudget = true;
        }

        return (SoftBudgetPage.Finish(
            page,
            moreItems: !exhausted,
            budgetHit: truncatedByBudget,
            () => FindRefsPageCursor.Encode(session.Epoch, entireSolution, nextDoc, nextLoc),
            tool,
            page.Count == 0 ? emptyMessage : completeMessage), null);
    }

    private static IReadOnlyList<IReadOnlyList<T>> AlignByDocument<T>(
        IReadOnlyList<Document> documents,
        IReadOnlyList<Document> scan,
        IReadOnlyList<IReadOnlyList<T>> aligned)
    {
        var map = new Dictionary<DocumentId, IReadOnlyList<T>>();
        for (var i = 0; i < scan.Count && i < aligned.Count; i++)
        {
            map[scan[i].Id] = aligned[i];
        }

        var filled = new IReadOnlyList<T>[documents.Count];
        for (var i = 0; i < documents.Count; i++)
        {
            filled[i] = map.TryGetValue(documents[i].Id, out var hits) ? hits : Array.Empty<T>();
        }

        return filled;
    }

    private static (List<T> Page, bool Exhausted, int NextDoc, int NextLoc) SliceByDocument<T>(
        IReadOnlyList<IReadOnlyList<T>> byDocument,
        int docIndex,
        int locOffset,
        int pageLimit)
    {
        var page = new List<T>();
        for (var i = docIndex; i < byDocument.Count; i++)
        {
            var hits = byDocument[i];
            var start = i == docIndex ? locOffset : 0;
            for (var loc = start; loc < hits.Count; loc++)
            {
                if (page.Count >= pageLimit)
                {
                    return (page, false, i, loc);
                }

                page.Add(hits[loc]);
            }
        }

        return (page, true, byDocument.Count, 0);
    }


    private async Task<(ImplementationItem? Item, SymbolQueryError? Error)> ToImplementationItemAsync(
        IWorkspaceSession session,
        Project project,
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        var success = ToSuccess(project, symbol);
        var locations = new List<SymbolLocation>();
        foreach (var location in symbol.Locations)
        {
            var (mapped, mapError) = await ToSymbolLocationAsync(
                    session, project, location, cancellationToken)
                .ConfigureAwait(false);
            if (mapError is not null)
            {
                return (null, mapError);
            }

            locations.Add(mapped!);
        }

        return (new ImplementationItem(success.Handle, success.Summary, locations), null);
    }

    private static Project ProjectForSymbol(Solution solution, ISymbol symbol, Project fallback)
    {
        var source = symbol.Locations.FirstOrDefault(l => l.IsInSource && l.SourceTree is not null);
        if (source?.SourceTree is { } tree)
        {
            var document = solution.GetDocument(tree);
            if (document is not null)
            {
                return document.Project;
            }
        }

        return fallback;
    }

    private async Task<IReadOnlyList<CallerLocationItem>> FlattenCallerHitsForDocumentAsync(
        IWorkspaceSession session,
        Document document,
        IEnumerable<SymbolCallerInfo> callers,
        CancellationToken cancellationToken)
    {
        var items = new List<CallerLocationItem>();
        foreach (var caller in callers)
        {
            if (!caller.IsDirect)
            {
                continue;
            }

            var owner = ProjectForSymbol(session.Solution, caller.CallingSymbol, document.Project);
            var success = ToSuccess(owner, caller.CallingSymbol);

            foreach (var location in caller.Locations)
            {
                if (!IsLocationInDocument(session.Solution, location, document))
                {
                    continue;
                }

                var (mapped, error) = await ToSymbolLocationAsync(
                        session, document.Project, location, cancellationToken)
                    .ConfigureAwait(false);
                if (error is not null)
                {
                    mapped = new SymbolLocation(
                        mapped?.DeclarationAvailability ?? DeclarationAvailability.InSource,
                        SymbolOrigin.Handwritten,
                        location.IsInSource ? location.SourceTree?.FilePath : null,
                        location.IsInSource ? location.SourceSpan.Start : null,
                        location.IsInSource ? location.SourceSpan.Length : null);
                }

                if (mapped is null ||
                    (mapped.DeclarationAvailability == DeclarationAvailability.None && mapped.FilePath is null))
                {
                    continue;
                }

                items.Add(new CallerLocationItem(
                    mapped.DeclarationAvailability,
                    mapped.Origin,
                    mapped.FilePath,
                    mapped.Start,
                    mapped.Length,
                    document.Project.Id.Id.ToString("D"),
                    success.Handle,
                    success.Summary));
            }
        }

        return items;
    }

    private async Task<IReadOnlyList<ReferenceLocationItem>> FlattenReferenceHitsForDocumentAsync(
        IWorkspaceSession session,
        Document document,
        IEnumerable<Microsoft.CodeAnalysis.FindSymbols.ReferencedSymbol> referencedSymbols,
        CancellationToken cancellationToken)
    {
        var solution = session.Solution;
        var items = new List<ReferenceLocationItem>();
        foreach (var referenced in referencedSymbols)
        {
            foreach (var defLocation in referenced.Definition.Locations)
            {
                if (!IsLocationInDocument(solution, defLocation, document))
                {
                    continue;
                }

                var item = await ToReferenceLocationItemAsync(
                        session,
                        document,
                        defLocation,
                        ReferenceLocationKind.Definition,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (item is not null)
                {
                    items.Add(item);
                }
            }

            foreach (var referenceLocation in referenced.Locations)
            {
                if (referenceLocation.Document.Id != document.Id)
                {
                    continue;
                }

                var item = await ToReferenceLocationItemAsync(
                        session,
                        document,
                        referenceLocation.Location,
                        ReferenceLocationKind.Reference,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (item is not null)
                {
                    items.Add(item);
                }
            }
        }

        return items;
    }

    private static bool IsLocationInDocument(Solution solution, Location location, Document document)
    {
        if (!location.IsInSource || location.SourceTree is null)
        {
            return false;
        }

        var doc = solution.GetDocument(location.SourceTree);
        return doc is not null && doc.Id == document.Id;
    }

    private async Task<ReferenceLocationItem?> ToReferenceLocationItemAsync(
        IWorkspaceSession session,
        Document document,
        Location location,
        string kind,
        CancellationToken cancellationToken)
    {
        var (mapped, error) = await ToSymbolLocationAsync(
                session, document.Project, location, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            // Soft-fail individual refs on driver failure: treat as handwritten span rather than
            // aborting the whole find-refs page. Attribution/goto paths surface the error instead.
            mapped = new SymbolLocation(
                mapped?.DeclarationAvailability ?? DeclarationAvailability.InSource,
                SymbolOrigin.Handwritten,
                location.IsInSource ? location.SourceTree?.FilePath : null,
                location.IsInSource ? location.SourceSpan.Start : null,
                location.IsInSource ? location.SourceSpan.Length : null);
        }

        if (mapped is null ||
            (mapped.DeclarationAvailability == DeclarationAvailability.None && mapped.FilePath is null))
        {
            return null;
        }

        return new ReferenceLocationItem(
            mapped.DeclarationAvailability,
            mapped.Origin,
            mapped.FilePath,
            mapped.Start,
            mapped.Length,
            document.Project.Id.Id.ToString("D"),
            kind);
    }

}
