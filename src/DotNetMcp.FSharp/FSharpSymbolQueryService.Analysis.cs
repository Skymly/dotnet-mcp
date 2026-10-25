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
        using var epochHold = EnterRequest(session.FSharpSnapshot.Epoch);
        var (item, project, check, error) = await TryResolveWithCheckAsync(session, handle, cancellationToken)
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
        var target = DefiningUse(check, item!)?.Symbol;

        return await ScanProjectsAsync<ReferenceLocationItem>(
            session,
            handle,
            project!,
            entireSolution,
            pageLimit,
            cursor,
            budget,
            "symbol_find_references",
            entireSolution ? "refs-entire" : "refs-closure",
            (scannedCheck, _, scanned) => ReferenceHits(
                scannedCheck, scanned, item!, target, session.FSharpSnapshot.Epoch),
            "No references were found.",
            cancellationToken).ConfigureAwait(false);
    }

    private List<ReferenceLocationItem> ReferenceHits(
        FSharpCheckProjectResults check,
        FSharpProjectSnapshot scanned,
        FSharpCatalogItem item,
        FSharpSymbol? target,
        long epoch)
    {
        var hits = new List<ReferenceLocationItem>();
        foreach (var use in check.GetAllUsesOfAllSymbols(null))
        {
            if (!SameSymbolUse(use.Symbol, item, target))
            {
                continue;
            }

            hits.Add(ToReference(
                epoch,
                scanned.ProjectId,
                use.FileName,
                use.Range,
                use.IsFromDefinition
                    ? ReferenceLocationKind.Definition
                    : ReferenceLocationKind.Reference));
        }

        return hits;
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

        var catalog = new List<FSharpCatalogItem>();
        foreach (var scanned in ScanScope(session.FSharpSnapshot, project!, entireSolution: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            catalog.AddRange(await CatalogAsync(
                    session.FSharpSnapshot, scanned, session.FSharpSnapshot.Epoch, cancellationToken)
                .ConfigureAwait(false));
        }

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

        var (page, pageError) = Page(impls, session.Epoch, pageLimit: limit, cursor, "symbol_find_implementations", handle,
            emptyMessage: "No implementations were found in the searched F# projects.");
        if (page is not null)
        {
            page = page with { Message = page.Message + ForeignDependentsNote(project!) };
        }

        return (page, pageError);
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

        // Ancestors live upstream: merge the catalogs of the defining project and
        // the F# projects it transitively references so base types/interfaces in
        // another F# project keep resolving instead of reporting as external.
        var catalog = new List<FSharpCatalogItem>();
        foreach (var scanned in DependencyScope(session.FSharpSnapshot, project!))
        {
            cancellationToken.ThrowIfCancellationRequested();
            catalog.AddRange(await CatalogAsync(
                    session.FSharpSnapshot, scanned, session.FSharpSnapshot.Epoch, cancellationToken)
                .ConfigureAwait(false));
        }

        var byName = new Dictionary<string, FSharpCatalogItem>(StringComparer.Ordinal);
        foreach (var candidate in catalog.Where(c => c.IsContainer))
        {
            byName.TryAdd(candidate.SignatureQualifiedName, candidate);
        }

        var chain = new List<HierarchyItem>();

        var current = item.BaseTypeName;
        while (!string.IsNullOrWhiteSpace(current) && byName.TryGetValue(current, out var parent))
        {
            var success = ToSuccess(parent);
            chain.Add(new HierarchyItem(HierarchyRelationKind.BaseType, success.Handle, success.Summary));
            current = parent.BaseTypeName;
        }

        var stoppedAt = string.IsNullOrWhiteSpace(current) ? null : current;
        var outsideInterfaces = new List<string>();
        foreach (var ifaceName in item.InterfaceNames ?? [])
        {
            if (!byName.TryGetValue(ifaceName, out var iface))
            {
                outsideInterfaces.Add($"'{ifaceName}'");
                continue;
            }

            var success = ToSuccess(iface);
            chain.Add(new HierarchyItem(HierarchyRelationKind.Interface, success.Handle, success.Summary));
        }

        var outsideNames = stoppedAt is null
            ? outsideInterfaces
            : [$"'{stoppedAt}'", .. outsideInterfaces];

        var (page, pageError) = Page(chain, session.Epoch, pageLimit: limit, cursor, "symbol_type_hierarchy", handle,
            emptyMessage: "Type has no base types or interfaces.");
        if (page is not null && outsideNames.Count > 0)
        {
            var head = stoppedAt is not null
                ? $"Base-type chain stops at {string.Join(", ", outsideNames)}"
                : (outsideNames.Count == 1 ? "External interface " : "External interfaces ") +
                  string.Join(", ", outsideNames);
            var verb = outsideNames.Count == 1 ? "is" : "are";
            var note =
                $"{head}, which {verb} outside the searched F# projects; " +
                "external base types and interfaces are not returned.";
            page = page with { Message = page.Items.Count == 0 ? note : page.Message + " " + note };
        }

        return (page, pageError);
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
        using var epochHold = EnterRequest(session.FSharpSnapshot.Epoch);
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

        var pageLimit = limit is null or < 1
            ? LanguageAdapters.DefaultMemberPageLimit
            : Math.Min(limit.Value, LanguageAdapters.MaxMemberPageLimit);
        var budget = softBudget ?? (entireSolution
            ? _softBudgets.FindRefsEntireSolution
            : _softBudgets.FindRefsScoped);
        var target = DefiningUse(check, item)?.Symbol;

        return await ScanProjectsAsync<CallerLocationItem>(
            session,
            handle,
            project!,
            entireSolution,
            pageLimit,
            cursor,
            budget,
            "symbol_find_callers",
            entireSolution ? "callers-entire" : "callers-closure",
            (scannedCheck, catalog, scanned) => CallerHits(
                scannedCheck, catalog, scanned, item!, target, session.FSharpSnapshot.Epoch),
            "No callers were found.",
            cancellationToken).ConfigureAwait(false);
    }

    private List<CallerLocationItem> CallerHits(
        FSharpCheckProjectResults check,
        IReadOnlyList<FSharpCatalogItem> items,
        FSharpProjectSnapshot scanned,
        FSharpCatalogItem item,
        FSharpSymbol? target,
        long epoch)
    {
        var catalog = FlattenCatalog(items).ToList();
        var uses = check.GetAllUsesOfAllSymbols(null).ToList();
        // Only definitions that bind to this project's own catalog can be callers —
        // filtering before FindEnclosingDefinition keeps a parameter or local def-use
        // from intercepting the enclosing member and dropping the caller (S10 finding).
        var definitions = uses
            .Where(u => u.IsFromDefinition)
            .Where(u => catalog.Any(c => SameSymbol(u.Symbol, c)))
            .ToList();
        var hits = new List<CallerLocationItem>();
        foreach (var use in uses)
        {
            if (use.IsFromDefinition || !SameSymbolUse(use.Symbol, item, target))
            {
                continue;
            }

            var enclosing = FindEnclosingDefinition(definitions, use, item);
            var callerItem = enclosing is null ? null : MatchCatalog(catalog, enclosing.Symbol);
            if (callerItem is null)
            {
                continue;
            }

            var success = ToSuccess(callerItem);
            var loc = ToLocation(epoch, use.FileName, use.Range);
            hits.Add(new CallerLocationItem(
                loc.DeclarationAvailability,
                loc.Origin,
                loc.FilePath,
                loc.Start,
                loc.Length,
                loc.Line,
                loc.Column,
                loc.EndLine,
                loc.EndColumn,
                scanned.ProjectId,
                success.Handle,
                success.Summary));
        }

        return hits;
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

        var (catalog, check, _) = await CheckProjectAsync(session.FSharpSnapshot, project, session.FSharpSnapshot.Epoch, cancellationToken).ConfigureAwait(false);
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
        if (symbol is FSharpMemberOrFunctionOrValue member)
        {
            if (member.DeclaringEntity is not { } declaringEntity)
            {
                return false;
            }

            var owner = declaringEntity.Value;
            var resolved = ResolveAccessor(member, owner) ?? member;
            var key = MemberFullName(owner, resolved);
            if (string.Equals(key, item.SignatureQualifiedName, StringComparison.Ordinal)
                || string.Equals(key + ":" + FormatReturnType(resolved), item.SignatureQualifiedName, StringComparison.Ordinal))
            {
                return true;
            }

            var loc = resolved.DeclarationLocation;
            return string.Equals(
                key + "@" + loc.StartLine + ":" + loc.StartColumn,
                item.SignatureQualifiedName,
                StringComparison.Ordinal);
        }

        var name = symbol is FSharpEntity entity ? EntityFullName(entity) : symbol.FullName;
        if (!string.IsNullOrWhiteSpace(name))
        {
            return string.Equals(name, item.SignatureQualifiedName, StringComparison.Ordinal);
        }

        return string.Equals(symbol.DisplayName, item.DisplayName, StringComparison.Ordinal)
            && SameDeclarationFile(symbol, item);
    }

    private static bool SameDeclarationFile(FSharpSymbol symbol, FSharpCatalogItem item)
    {
        if (symbol.DeclarationLocation is not { } declarationLocation)
        {
            return false;
        }

        var declaredFile = declarationLocation.Value.FileName;
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

    private ReferenceLocationItem ToReference(long epoch, string projectId, string file, FcsRange range, string kind)
    {
        var loc = ToLocation(epoch, file, range);
        return new ReferenceLocationItem(
            loc.DeclarationAvailability,
            loc.Origin,
            loc.FilePath,
            loc.Start,
            loc.Length,
            loc.Line,
            loc.Column,
            loc.EndLine,
            loc.EndColumn,
            projectId,
            kind);
    }

    private SymbolLocation ToLocation(long epoch, string file, FcsRange range)
    {
        if (TryGetSnapshot(epoch, file, out var path, out var text))
        {
            var (start, length) = ToSpan(text, range);
            return new SymbolLocation(
                DeclarationAvailability.InSource,
                SymbolOrigin.Handwritten,
                path,
                start,
                length,
                range.StartLine,
                range.StartColumn + 1,
                range.EndLine,
                range.EndColumn + 1);
        }

        // No snapshot text: keep FilePath but leave every coordinate null — a
        // location never mixes null and non-null coordinate systems.
        return new SymbolLocation(
            DeclarationAvailability.InSource,
            SymbolOrigin.Handwritten,
            string.IsNullOrWhiteSpace(file) ? null : file,
            null,
            null,
            null,
            null,
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

    /// <summary>
    /// Cross-project scan with project-stage paging: the cursor's document index is
    /// the index into the ordered scope project list, its location offset pages the
    /// hits of that project. The budget is checked before scanning the next project —
    /// the project at the resumed stage always completes so the cursor advances.
    /// Completed scans are cached per (epoch, handle, scope) like the Roslyn finder.
    /// </summary>
    private async Task<(PagedResult<T>? Success, SymbolQueryError? Error)> ScanProjectsAsync<T>(
        IWorkspaceSession session,
        string handle,
        FSharpProjectSnapshot defining,
        bool entireSolution,
        int pageLimit,
        string? cursor,
        TimeSpan budget,
        string tool,
        string scopeKey,
        Func<FSharpCheckProjectResults, IReadOnlyList<FSharpCatalogItem>, FSharpProjectSnapshot, IReadOnlyList<T>> collect,
        string emptyMessage,
        CancellationToken cancellationToken)
    {
        if (!SoftBudgetPage.TryReadFindRefs(
                cursor,
                session.Epoch,
                entireSolution,
                tool,
                handle,
                out var stage,
                out var locOffset,
                out var cursorError,
                scopeMismatchMessage: "Cursor scope does not match the entireSolution parameter for this request."))
        {
            return (null, cursorError);
        }

        var scope = ScanScope(session.FSharpSnapshot, defining, entireSolution);
        if (stage > scope.Count || (stage == scope.Count && locOffset > 0))
        {
            return (null, new StaleCursorError(
                "Cursor project index is past the end of the scoped project list.",
                $"Call {tool} again without a cursor to start a fresh page."));
        }

        var epoch = session.FSharpSnapshot.Epoch;
        var cache = session as IWorkspaceSessionCaches;
        var truncatedByBudget = false;
        var scannedThrough = scope.Count;
        if (cache?.FindHits.TryGetByDocument(session.Epoch, handle, scopeKey, out IReadOnlyList<IReadOnlyList<T>>? byProject) != true ||
            byProject is null)
        {
            var filled = new IReadOnlyList<T>[scope.Count];
            for (var i = 0; i < filled.Length; i++)
            {
                filled[i] = [];
            }

            var clock = Stopwatch.StartNew();
            var index = stage;
            for (; index < scope.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index > stage && clock.Elapsed >= budget)
                {
                    truncatedByBudget = true;
                    break;
                }

                var scanned = scope[index];
                var (items, scannedCheck, _) = await CheckProjectAsync(
                        session.FSharpSnapshot, scanned, epoch, cancellationToken)
                    .ConfigureAwait(false);
                filled[index] = scannedCheck is null
                    ? []
                    : collect(scannedCheck, items, scanned);
            }

            scannedThrough = index;
            byProject = filled;
            if (!truncatedByBudget && stage == 0 && locOffset == 0)
            {
                cache?.FindHits.SetByDocument(session.Epoch, handle, scopeKey, byProject);
            }
        }
        else
        {
            scannedThrough = scope.Count;
        }

        if (stage < scannedThrough && locOffset > byProject![stage].Count)
        {
            return (null, new StaleCursorError(
                "Cursor location offset is past the end of hits for a project stage.",
                $"Call {tool} again without a cursor to start a fresh page."));
        }

        var (page, exhausted, nextStage, nextLoc) = FinderDocumentScan.Slice(
            byProject!, stage, locOffset, pageLimit, scannedThrough);
        var result = SoftBudgetPage.Finish(
            page,
            moreItems: !exhausted,
            budgetHit: truncatedByBudget,
            () => FindRefsPageCursor.Encode(session.Epoch, entireSolution, nextStage, nextLoc, tool, handle),
            tool,
            page.Count == 0 ? emptyMessage : "Page complete.");
        return (result with { Message = result.Message + ForeignDependentsNote(defining) }, null);
    }

    /// <summary>
    /// Ordered scan list: default scope is the defining project then its F#
    /// dependents (transitive, alphabetical); entireSolution is every F# project.
    /// Ordering is deterministic so a cursor's project index survives across calls.
    /// </summary>
    private static IReadOnlyList<FSharpProjectSnapshot> ScanScope(
        FSharpWorkspaceSnapshot snapshot,
        FSharpProjectSnapshot defining,
        bool entireSolution)
    {
        if (entireSolution)
        {
            return snapshot.Projects
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ThenBy(p => p.ProjectId, StringComparer.Ordinal)
                .ToArray();
        }

        return
        [
            defining,
            .. FSharpDependents(snapshot, defining)
        ];
    }

    private static IEnumerable<FSharpProjectSnapshot> FSharpDependents(
        FSharpWorkspaceSnapshot snapshot,
        FSharpProjectSnapshot defining) =>
        defining.DependentProjects
            .Where(d => string.Equals(d.Language, LanguageAdapters.FSharpLanguage, StringComparison.Ordinal))
            .Select(d => snapshot.FindProject(d.ProjectId))
            .OfType<FSharpProjectSnapshot>()
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.ProjectId, StringComparer.Ordinal);

    /// <summary>The transitive F# project-reference closure of the defining project
    /// (where base types and interfaces live), defining project first.</summary>
    private static IReadOnlyList<FSharpProjectSnapshot> DependencyScope(
        FSharpWorkspaceSnapshot snapshot,
        FSharpProjectSnapshot defining)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { defining.ProjectId };
        var scope = new List<FSharpProjectSnapshot> { defining };
        var frontier = new Queue<FSharpProjectSnapshot>();
        frontier.Enqueue(defining);
        while (frontier.Count > 0)
        {
            foreach (var reference in frontier.Dequeue().ProjectReferences)
            {
                if (!seen.Add(reference.ProjectId))
                {
                    continue;
                }

                var dep = snapshot.FindProject(reference.ProjectId);
                if (dep is not null)
                {
                    scope.Add(dep);
                    frontier.Enqueue(dep);
                }
            }
        }

        return scope;
    }

    /// <summary>Dependent projects that are not F# can never be searched — name the gap.</summary>
    private static string ForeignDependentsNote(FSharpProjectSnapshot project)
    {
        var foreign = project.DependentProjects
            .Where(d => !string.Equals(d.Language, LanguageAdapters.FSharpLanguage, StringComparison.Ordinal))
            .ToList();
        return foreign.Count == 0
            ? string.Empty
            : $" C#/VB dependent projects were not searched: {DescribeDependents(foreign)}.";
    }

    private static FSharpSymbolUse? DefiningUse(FSharpCheckProjectResults? check, FSharpCatalogItem item) =>
        check?.GetAllUsesOfAllSymbols(null)
            .FirstOrDefault(use => use.IsFromDefinition && SameSymbol(use.Symbol, item));

    /// <summary>Route-A checks hand the defining symbol across projects: logical
    /// identity is the primary match, the fully-qualified catalog signature is the
    /// fallback — property uses bind accessor symbols which are not
    /// IsEffectivelySameAs-equal to the resolved property.</summary>
    private static bool SameSymbolUse(FSharpSymbol symbol, FSharpCatalogItem item, FSharpSymbol? target) =>
        (target is not null && symbol.IsEffectivelySameAs(target)) || SameSymbol(symbol, item);
}
