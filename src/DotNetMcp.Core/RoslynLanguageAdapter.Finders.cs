using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;

namespace DotNetMcp.Core;

public sealed partial class RoslynLanguageAdapter
{
    // Find-references paging lanes: stage 0 walks scoped documents, stage 1
    // walks the Exact XAML usage list once source hits are exhausted.
    private const int XamlReferencesStage = 1;

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
                handle,
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
            () => MemberPageCursor.Encode(epoch, nextOffset, "symbol_find_implementations", handle),
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
            handle,
            "Type has no base types or interfaces.",
            "Type hierarchy page complete.",
            "the type hierarchy");
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
        var requested = softBudget ?? (entireSolution
            ? _softBudgets.FindRefsEntireSolution
            : _softBudgets.FindRefsScoped);
        var budget = FinderDocumentScan.ResolveBudget(
            requested,
            entireSolution
                ? SoftBudgetOptions.Default.FindRefsEntireSolution
                : SoftBudgetOptions.Default.FindRefsScoped);
        if (!SoftBudgetPage.TryReadFindRefs(
                cursor,
                session.Epoch,
                entireSolution,
                "symbol_find_callers",
                handle,
                out var docIndex,
                out var locOffset,
                out _,
                out var cursorError,
                scopeMismatchMessage: "Cursor scope does not match the entireSolution parameter for this request."))
        {
            return (null, cursorError);
        }

        var documents = await OrderedScopedDocumentsAsync(solution, project!, entireSolution, cancellationToken)
            .ConfigureAwait(false);

        return await PageFinderHitsAsync<CallerLocationItem>(
                session,
                handle,
                scopeKey: entireSolution ? "callers-entire" : "callers-closure",
                entireSolution,
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
        const string emptyMessage = "No references found in the selected scope.";
        const string completeMessage = "Reference page complete.";
        var solution = session.Solution;
        var (project, symbol, error) = await TryResolveHandleAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        var pageLimit = ClampLimit(limit);
        var requested = softBudget ?? (entireSolution
            ? _softBudgets.FindRefsEntireSolution
            : _softBudgets.FindRefsScoped);
        var budget = FinderDocumentScan.ResolveBudget(
            requested,
            entireSolution
                ? SoftBudgetOptions.Default.FindRefsEntireSolution
                : SoftBudgetOptions.Default.FindRefsScoped);
        if (!SoftBudgetPage.TryReadFindRefs(
                cursor,
                session.Epoch,
                entireSolution,
                "symbol_find_references",
                handle,
                out var docIndex,
                out var locOffset,
                out var stage,
                out var cursorError,
                scopeMismatchMessage: "Cursor scope does not match the entireSolution parameter for this request."))
        {
            return (null, cursorError);
        }

        var xamlSource = (session as IXamlUsageSourceProvider)?.XamlUsageSource;
        if (stage != 0)
        {
            // Stage-1 cursors are only issued while a XAML source is wired;
            // anything else is stale for this session.
            if (stage != XamlReferencesStage || xamlSource is null)
            {
                return (null, SoftBudgetPage.PastEnd("symbol_find_references", "the XAML usage list"));
            }

            var scoped = await OrderedScopedDocumentsAsync(solution, project!, entireSolution, cancellationToken)
                .ConfigureAwait(false);
            return await PageXamlReferenceHitsAsync(
                    session,
                    symbol!,
                    handle,
                    entireSolution,
                    xamlSource,
                    scoped,
                    budget,
                    pageLimit,
                    locOffset,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // One wall-clock shared by the source scan and the XAML lane: whatever
        // the document walk already spent does not reset for stage 1.
        var clock = Stopwatch.StartNew();
        var documents = await OrderedScopedDocumentsAsync(solution, project!, entireSolution, cancellationToken)
            .ConfigureAwait(false);

        var (slice, scanError) = await ScanFinderHitsAsync<ReferenceLocationItem>(
                session,
                handle,
                scopeKey: entireSolution ? "refs-entire" : "refs-closure",
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
                tool: "symbol_find_references",
                cancellationToken)
            .ConfigureAwait(false);
        if (scanError is not null)
        {
            return (null, scanError);
        }

        if (!slice!.Exhausted)
        {
            return (SoftBudgetPage.Finish(
                slice.Items,
                moreItems: true,
                budgetHit: slice.TruncatedByBudget,
                () => FindRefsPageCursor.Encode(
                    session.Epoch, entireSolution, slice.NextDoc, slice.NextLoc,
                    "symbol_find_references", handle),
                "symbol_find_references",
                slice.Items.Count == 0 ? emptyMessage : completeMessage), null);
        }

        if (xamlSource is null)
        {
            return (SoftBudgetPage.Finish(
                slice.Items,
                moreItems: false,
                budgetHit: false,
                static () => string.Empty,
                "symbol_find_references",
                slice.Items.Count == 0 ? emptyMessage : completeMessage), null);
        }

        // Stage 1: Exact XAML usage points follow source hits on the same page.
        var room = Math.Max(pageLimit - slice.Items.Count, 0);
        var (xamlItems, xamlIncomplete) = await CollectXamlReferenceHitsAsync(
                session,
                symbol!,
                xamlSource,
                documents,
                budget,
                clock,
                deliverableFloor: room - 1,
                cancellationToken)
            .ConfigureAwait(false);
        var taken = Math.Min(room, xamlItems.Count);
        for (var i = 0; i < taken; i++)
        {
            slice.Items.Add(xamlItems[i]);
        }

        return (SoftBudgetPage.Finish(
            slice.Items,
            moreItems: xamlIncomplete || taken < xamlItems.Count,
            budgetHit: xamlIncomplete,
            () => FindRefsPageCursor.Encode(
                session.Epoch, entireSolution, 0, taken,
                "symbol_find_references", handle, stage: XamlReferencesStage),
            "symbol_find_references",
            slice.Items.Count == 0 ? emptyMessage : completeMessage), null);
    }

    private async Task<(PagedResult<ReferenceLocationItem>? Success, SymbolQueryError? Error)> PageXamlReferenceHitsAsync(
        IWorkspaceSession session,
        ISymbol symbol,
        string handle,
        bool entireSolution,
        IXamlUsageSource xamlSource,
        IReadOnlyList<Document> documents,
        TimeSpan budget,
        int pageLimit,
        int locOffset,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var (items, incomplete) = await CollectXamlReferenceHitsAsync(
                session,
                symbol,
                xamlSource,
                documents,
                budget,
                clock,
                deliverableFloor: locOffset,
                cancellationToken)
            .ConfigureAwait(false);
        if (locOffset > items.Count)
        {
            return (null, SoftBudgetPage.PastEnd("symbol_find_references", "the XAML usage list"));
        }

        var page = items.Skip(locOffset).Take(pageLimit).ToList();
        var next = locOffset + page.Count;
        return (SoftBudgetPage.Finish(
            page,
            moreItems: next < items.Count || incomplete,
            budgetHit: incomplete,
            () => FindRefsPageCursor.Encode(
                session.Epoch, entireSolution, 0, next,
                "symbol_find_references", handle, stage: XamlReferencesStage),
            "symbol_find_references",
            page.Count == 0
                ? "No references found in the selected scope."
                : "Reference page complete."), null);
    }

    /// <summary>
    /// Stage-1 XAML collection: scoped projects in deterministic ProjectId order,
    /// concatenating each project's index-ordered usage points, filtered to
    /// <see cref="XamlUsageConfidence.Exact"/> matches on the normalized
    /// signature key, and deduplicated by normalized path/span/kind/target so a
    /// shared or linked XAML document seen through two projects counts once.
    /// The budget check runs at project granularity and may only cut the tail
    /// once more than <paramref name="deliverableFloor"/> items are collected —
    /// the same contract as the document scan finishing its in-flight document,
    /// so a resumed page always makes progress.
    /// </summary>
    private static async Task<(List<ReferenceLocationItem> Items, bool Incomplete)> CollectXamlReferenceHitsAsync(
        IWorkspaceSession session,
        ISymbol symbol,
        IXamlUsageSource xamlSource,
        IReadOnlyList<Document> documents,
        TimeSpan budget,
        Stopwatch clock,
        int deliverableFloor,
        CancellationToken cancellationToken)
    {
        var targetKey = SymbolKey(NormalizePositionSymbol(symbol));
        var projectIds = documents
            .Select(static d => d.Project.Id.Id)
            .Distinct()
            .OrderBy(static id => id.ToString("D"), StringComparer.Ordinal)
            .ToArray();

        var items = new List<ReferenceLocationItem>();
        var seen = new HashSet<(string Path, int Start, int Length, XamlUsageKind Kind, string SymbolKey)>();
        var incomplete = false;
        for (var i = 0; i < projectIds.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (i > 0 && clock.Elapsed >= budget && items.Count > deliverableFloor)
            {
                incomplete = true;
                break;
            }

            var usages = await xamlSource
                .GetUsagesAsync(session, projectIds[i].ToString("D"), cancellationToken)
                .ConfigureAwait(false);
            foreach (var usage in usages)
            {
                if (usage.Confidence != XamlUsageConfidence.Exact ||
                    !string.Equals(usage.SymbolKey, targetKey, StringComparison.Ordinal))
                {
                    continue;
                }

                var key = (PositionPaths.Key(usage.FilePath), usage.Start, usage.Length, usage.Kind, usage.SymbolKey);
                if (!seen.Add(key))
                {
                    continue;
                }

                items.Add(ToXamlReferenceItem(usage));
            }
        }

        return (items, incomplete);
    }

    private static ReferenceLocationItem ToXamlReferenceItem(XamlUsagePoint usage) =>
        new(
            DeclarationAvailability.InSource,
            SymbolOrigin.Handwritten,
            usage.FilePath,
            usage.Start,
            usage.Length,
            usage.Line,
            usage.Column,
            usage.EndLine,
            usage.EndColumn,
            usage.ProjectId,
            ReferenceLocationKind.Reference,
            usage.Kind);

    private static async Task<List<Document>> OrderedScopedDocumentsAsync(
        Solution solution,
        Project project,
        bool entireSolution,
        CancellationToken cancellationToken)
    {
        var scope = entireSolution
            ? FindRefsScopeKind.EntireSolution
            : FindRefsScopeKind.DependencyClosure;
        var documents = await FindRefsScopes
            .DocumentsForScopeAsync(solution, project, scope, cancellationToken)
            .ConfigureAwait(false);
        return documents
            .OrderBy(d => d.Project.Name, StringComparer.Ordinal)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ThenBy(d => d.Id.Id)
            .ToList();
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
        var (slice, error) = await ScanFinderHitsAsync<T>(
                session,
                handle,
                scopeKey,
                documents,
                budget,
                pageLimit,
                docIndex,
                locOffset,
                findAligned,
                tool,
                cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        return (SoftBudgetPage.Finish(
            slice!.Items,
            moreItems: !slice.Exhausted,
            budgetHit: slice.TruncatedByBudget,
            () => FindRefsPageCursor.Encode(session.Epoch, entireSolution, slice.NextDoc, slice.NextLoc, tool, handle),
            tool,
            slice.Items.Count == 0 ? emptyMessage : completeMessage), null);
    }

    private async Task<(FinderSlice<T>? Slice, SymbolQueryError? Error)> ScanFinderHitsAsync<T>(
        IWorkspaceSession session,
        string handle,
        string scopeKey,
        IReadOnlyList<Document> documents,
        TimeSpan budget,
        int pageLimit,
        int docIndex,
        int locOffset,
        Func<IReadOnlyList<Document>, CancellationToken, Task<IReadOnlyList<IReadOnlyList<T>>>> findAligned,
        string tool,
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
        var scannedThrough = documents.Count;

        if (!fromCache)
        {
            try
            {
                var scan = await FinderDocumentScan.ScanAsync(
                        documents,
                        docIndex,
                        budget,
                        findAligned,
                        cancellationToken)
                    .ConfigureAwait(false);
                byDocument = scan.ByDocument;
                truncatedByBudget = scan.TruncatedByBudget;
                scannedThrough = scan.ScannedThrough;
                if (!truncatedByBudget && docIndex == 0 && locOffset == 0)
                {
                    cache?.FindHits.SetByDocument(session.Epoch, handle, scopeKey, scan.ByDocument);
                }
            }
            catch (UnreconciledGeneratedLocationException ex)
            {
                return (null, ex.Error);
            }
        }

        if (docIndex < scannedThrough && locOffset > byDocument![docIndex].Count)
        {
            return (null, new StaleCursorError(
                "Cursor location offset is past the end of hits for a document.",
                $"Call {tool} again without a cursor to start a fresh page."));
        }

        var (page, exhausted, nextDoc, nextLoc) = FinderDocumentScan.Slice(
            byDocument!, docIndex, locOffset, pageLimit, scannedThrough);
        return (new FinderSlice<T>(page, exhausted, nextDoc, nextLoc, truncatedByBudget), null);
    }

    private sealed record FinderSlice<T>(
        List<T> Items,
        bool Exhausted,
        int NextDoc,
        int NextLoc,
        bool TruncatedByBudget);

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
                    throw new UnreconciledGeneratedLocationException(error);
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
                    mapped.Line,
                    mapped.Column,
                    mapped.EndLine,
                    mapped.EndColumn,
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
            // Generated locations that cannot be reconciled must not be reported as Handwritten.
            throw new UnreconciledGeneratedLocationException(error);
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
            mapped.Line,
            mapped.Column,
            mapped.EndLine,
            mapped.EndColumn,
            document.Project.Id.Id.ToString("D"),
            kind);
    }

    private sealed class UnreconciledGeneratedLocationException : Exception
    {
        public UnreconciledGeneratedLocationException(SymbolQueryError error)
        {
            Error = error;
        }

        public SymbolQueryError Error { get; }
    }

}
