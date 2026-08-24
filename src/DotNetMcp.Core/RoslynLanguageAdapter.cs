using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;

namespace DotNetMcp.Core;

public sealed class RoslynLanguageAdapter : ILanguageAdapter
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

    private readonly GeneratorQueryService _generators;
    private readonly SoftBudgetOptions _softBudgets;
    public RoslynLanguageAdapter(
        GeneratorQueryService generators,
        SoftBudgetOptions? softBudgets = null)
    {
        _generators = generators;
        _softBudgets = softBudgets ?? SoftBudgetOptions.Default;
    }

    public bool OwnsLanguage(string languageToken) =>
        SymbolQueryService.IsSupportedLanguageToken(languageToken);

    public bool OwnsProject(Project project) =>
        SymbolQueryService.IsSupportedRoslynLanguage(project.Language);

    public bool SupportsCodeRefactoring => true;

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

        var solution = session.Solution;
        var projects = FilterProjects(solution, projectId, out var projectFilterError);
        if (projectFilterError is not null)
        {
            return (null, projectFilterError);
        }

        var query = name.Trim();
        var matches = new List<(Project Project, ISymbol Symbol)>();
        if (string.IsNullOrWhiteSpace(projectId))
        {
            await CollectResolveMatchesAsync(session, projects, query, matches, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            foreach (var project in projects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await AddResolveMatchesAsync(session, project, query, matches, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (matches.Count == 0)
        {
            return (null, new SymbolNotFoundError(
                $"No symbol named '{name}' was found in the ready workspace.",
                "Confirm the name/FQN, or pass projectId from workspace_list_projects, then call symbol_resolve again."));
        }

        // Deduplicate identical symbols within the same project (walk may hit twice).
        matches = matches
            .DistinctBy(m => (m.Project.Id.Id, SymbolKey(m.Symbol)))
            .ToList();

        // A referenced copy in another language/project is not a second definition.
        if (matches.Count > 1)
        {
            var sourceDefining = matches
                .Where(m => m.Symbol.Locations.Any(static l => l.IsInSource))
                .ToList();
            if (sourceDefining.Count == 1)
            {
                matches = sourceDefining;
            }
        }

        if (matches.Count > 1)
        {
            var ids = string.Join(", ", matches.Select(m => m.Project.Id.Id.ToString("D")).Distinct());
            return (null, new SymbolAmbiguousError(
                $"Symbol '{name}' matched {matches.Count} candidates across projectId(s): {ids}.",
                "Pass projectId (and a more specific FQN if needed) to symbol_resolve to disambiguate."));
        }

        var (projectHit, symbolHit) = matches[0];
        return (ToSuccess(projectHit, symbolHit), null);
    }

    public async Task<(SymbolResolveSuccess? Success, SymbolQueryError? Error)> GetSummaryAsync(
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken = default)
    {

        var (project, symbol, error) = await TryResolveHandleAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        return (ToSuccess(project!, symbol!), null);
    }

    /// <summary>
    /// Resolve a type SymbolHandle and look up an instance property or field by name.
    /// Returns the member and its type so callers can walk a Binding path in-process.
    /// </summary>
    internal async Task<(TypeMemberLookup? Success, SymbolQueryError? Error)> LookupTypeMemberAsync(
        IWorkspaceSession session,
        string typeHandle,
        string memberName,
        CancellationToken cancellationToken = default)
    {
        var (project, symbol, error) = await TryResolveHandleAsync(session, typeHandle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        if (symbol is not ITypeSymbol type)
        {
            return (null, new SymbolNotFoundError(
                "Handle does not refer to a type; member lookup requires a type SymbolHandle.",
                "Call symbol_resolve for a type name/FQN, then look up members on that handle."));
        }

        return LookupTypeMember(project!, type, memberName, publicOnly: true);
    }

    internal async Task<(TypeMemberLookup? Success, SymbolQueryError? Error)> LookupTypeMemberAsync(
        IWorkspaceSession session,
        string typeHandle,
        string memberName,
        bool publicOnly,
        CancellationToken cancellationToken = default)
    {
        var (project, symbol, error) = await TryResolveHandleAsync(session, typeHandle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        if (symbol is not ITypeSymbol type)
        {
            return (null, new SymbolNotFoundError(
                "Handle does not refer to a type; member lookup requires a type SymbolHandle.",
                "Call symbol_resolve for a type name/FQN, then look up members on that handle."));
        }

        return LookupTypeMember(project!, type, memberName, publicOnly);
    }

    /// <summary>
    /// Continue a Binding-path walk from an already-resolved type without a handle round-trip.
    /// </summary>
    internal (TypeMemberLookup? Success, SymbolQueryError? Error) LookupTypeMember(
        Project project,
        ITypeSymbol type,
        string memberName,
        bool publicOnly = true)
    {
        if (string.IsNullOrWhiteSpace(memberName))
        {
            return (null, new MemberNotFoundError(
                "Member name is empty.",
                "Pass a property or field name from the Binding path."));
        }

        var name = memberName.Trim();
        ISymbol? found = null;
        for (var current = type; current is not null; current = current.BaseType)
        {
            var candidates = current.GetMembers(name)
                .Where(m => !m.IsStatic && !m.IsImplicitlyDeclared)
                .Where(m => !publicOnly || m.DeclaredAccessibility == Accessibility.Public)
                .ToArray();

            found = candidates.OfType<IPropertySymbol>().FirstOrDefault(p => p.Parameters.Length == 0)
                ?? (ISymbol?)candidates.OfType<IFieldSymbol>().FirstOrDefault();
            if (found is not null)
            {
                break;
            }
        }

        if (found is null)
        {
            return (null, new MemberNotFoundError(
                $"Type '{type.ToDisplayString(SymbolDisplayFormats.SignatureQualified)}' has no public instance property or field named '{name}'.",
                "Check the Binding path segment against the type's public instance properties and fields."));
        }

        var memberType = found switch
        {
            IPropertySymbol property => property.Type,
            IFieldSymbol field => field.Type,
            _ => null
        };

        if (memberType is null)
        {
            return (null, new MemberNotFoundError(
                $"Member '{name}' on '{type.ToDisplayString(SymbolDisplayFormats.SignatureQualified)}' has no resolvable type.",
                "Check the Binding path segment against the type's public instance properties and fields."));
        }

        return (new TypeMemberLookup(found, memberType, project), null);
    }

    internal string FormatHandle(Project project, ISymbol symbol) =>
        ToSuccess(project, symbol).Handle;

    internal Task<(Project? Project, ISymbol? Symbol, SymbolQueryError? Error)> ResolveHandleSymbolAsync(
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken = default) =>
        TryResolveHandleAsync(session, handle, cancellationToken);

    public async Task<(SymbolDefinitionSuccess? Success, SymbolQueryError? Error)> GetDefinitionAsync(
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken = default)
    {

        var (project, symbol, error) = await TryResolveHandleAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        var locations = new List<SymbolLocation>();
        foreach (var location in symbol!.Locations)
        {
            var (mapped, mapError) = await ToSymbolLocationAsync(
                    session, project!, location, cancellationToken)
                .ConfigureAwait(false);
            if (mapError is not null)
            {
                return (null, mapError);
            }

            locations.Add(mapped!);
        }

        if (locations.Count == 0)
        {
            return (null, new DefinitionNotFoundError(
                $"No definition locations were found for '{symbol.ToDisplayString(SymbolDisplayFormats.SignatureQualified)}'.",
                "Confirm the handle with symbol_summary, or call symbol_resolve for a source symbol."));
        }

        return (new SymbolDefinitionSuccess(locations), null);
    }

    public async Task<(SymbolAttributionSuccess? Success, SymbolQueryError? Error)> GetAttributionAsync(
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken = default)
    {
        var (project, symbol, error) = await TryResolveHandleAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        var (attribution, attrError) = await AttributeSymbolAsync(
                session, project!, symbol!, cancellationToken)
            .ConfigureAwait(false);
        if (attrError is not null)
        {
            return (null, attrError);
        }

        var members = new Dictionary<string, SymbolAttribution>(StringComparer.Ordinal);
        if (symbol is INamedTypeSymbol type)
        {
            foreach (var member in type.GetMembers()
                         .Where(m => !m.IsImplicitlyDeclared)
                         .Where(static m => m is not IMethodSymbol
                         {
                             MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd
                             or MethodKind.EventRemove or MethodKind.EventRaise
                         })
                         .OrderBy(SymbolKey, StringComparer.Ordinal))
            {
                var (memberAttr, memberError) = await AttributeSymbolAsync(
                        session, project!, member, cancellationToken)
                    .ConfigureAwait(false);
                if (memberError is not null)
                {
                    return (null, memberError);
                }

                members[SymbolKey(member)] = memberAttr!;
            }
        }

        return (new SymbolAttributionSuccess(attribution!, members), null);
    }

    public async Task<(PagedResult<MemberListItem>? Success, SymbolQueryError? Error)> GetMembersAsync(
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
                "Handle does not refer to a named type; member lists require a type SymbolHandle.",
                "Call symbol_resolve for a type name/FQN, then call symbol_members with that handle."));
        }

        var pageLimit = ClampLimit(limit);
        var items = type.GetMembers()
            .Where(m => m.Kind is not SymbolKind.NamedType and not SymbolKind.Namespace)
            .Where(m => !m.IsImplicitlyDeclared)
            .OrderBy(SymbolKey, StringComparer.Ordinal)
            .Select(m =>
            {
                var success = ToSuccess(project!, m);
                return new MemberListItem(success.Handle, success.Summary);
            })
            .ToList();

        return SoftBudgetPage.Page(
            items,
            epoch,
            budgetHit: false,
            cursor,
            pageLimit,
            "symbol_members",
            "Type has no listable members.",
            "Member page complete.",
            "the member list");
    }

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

    private async Task<(SymbolAttribution? Attribution, SymbolQueryError? Error)> AttributeSymbolAsync(
        IWorkspaceSession session,
        Project project,
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        if (symbol.Locations.Length == 0)
        {
            return (new SymbolAttribution(DeclarationAvailability.None, SymbolOrigin.Handwritten, null), null);
        }

        if (symbol.Locations.All(l => l.IsInMetadata))
        {
            return (new SymbolAttribution(DeclarationAvailability.InMetadata, SymbolOrigin.Handwritten, null), null);
        }

        var declaring = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        SyntaxTree? tree;
        if (declaring is null)
        {
            var anySource = symbol.Locations.FirstOrDefault(l => l.IsInSource);
            if (anySource is null)
            {
                return (new SymbolAttribution(DeclarationAvailability.None, SymbolOrigin.Handwritten, null), null);
            }

            tree = anySource.SourceTree;
        }
        else
        {
            tree = declaring.SyntaxTree;
        }

        var (originLabel, originError) = await ResolveOriginAsync(
                session,
                project,
                tree,
                cancellationToken)
            .ConfigureAwait(false);
        if (originError is not null)
        {
            return (null, originError);
        }

        return (ToAttribution(DeclarationAvailability.InSource, originLabel), null);
    }

    private static SymbolAttribution ToAttribution(string availability, string? originLabel)
    {
        if (originLabel is not null &&
            originLabel.StartsWith(SymbolOrigin.SourceGenerator + "(", StringComparison.Ordinal) &&
            TryParseSourceGeneratorOrigin(originLabel, out var identity))
        {
            return new SymbolAttribution(availability, SymbolOrigin.SourceGenerator, identity);
        }

        return new SymbolAttribution(availability, SymbolOrigin.Handwritten, null);
    }

    private static bool TryParseSourceGeneratorOrigin(string origin, out GeneratorIdentity identity)
    {
        identity = new GeneratorIdentity(string.Empty, string.Empty, string.Empty);
        var prefix = SymbolOrigin.SourceGenerator + "(";
        if (!origin.StartsWith(prefix, StringComparison.Ordinal) || !origin.EndsWith(')'))
        {
            return false;
        }

        var inner = origin[prefix.Length..^1];
        var at = inner.LastIndexOf('@');
        var sep = inner.IndexOf("::", StringComparison.Ordinal);
        if (at <= 0 || sep <= 0 || at <= sep + 2)
        {
            return false;
        }

        identity = new GeneratorIdentity(
            inner[..sep],
            inner[(sep + 2)..at],
            inner[(at + 1)..]);
        return true;
    }

    private async Task<(SymbolLocation? Location, SymbolQueryError? Error)> ToSymbolLocationAsync(
        IWorkspaceSession session,
        Project project,
        Location location,
        CancellationToken cancellationToken)
    {
        if (location.IsInMetadata)
        {
            return (new SymbolLocation(
                DeclarationAvailability.InMetadata,
                Origin: null,
                FilePath: null,
                Start: null,
                Length: null), null);
        }

        if (!location.IsInSource)
        {
            return (new SymbolLocation(
                DeclarationAvailability.None,
                Origin: null,
                FilePath: null,
                Start: null,
                Length: null), null);
        }

        var tree = location.SourceTree;
        var span = location.SourceSpan;
        var path = tree?.FilePath;
        var (origin, originError) = await ResolveOriginAsync(session, project, tree, cancellationToken)
            .ConfigureAwait(false);
        if (originError is not null)
        {
            return (null, originError);
        }

        return (new SymbolLocation(
            DeclarationAvailability.InSource,
            origin,
            path,
            span.Start,
            span.Length), null);
    }

    private async Task<(string? Origin, SymbolQueryError? Error)> ResolveOriginAsync(
        IWorkspaceSession session,
        Project project,
        SyntaxTree? tree,
        CancellationToken cancellationToken)
    {
        if (tree is null)
        {
            return (SymbolOrigin.Handwritten, null);
        }

        var projectId = project.Id.Id.ToString("D");
        var (identity, matchError) = await _generators
            .MatchSyntaxTreeAsync(session, projectId, tree, cancellationToken)
            .ConfigureAwait(false);
        if (matchError is not null)
        {
            return (null, matchError);
        }

        if (identity is not null)
        {
            return (SymbolOrigin.FormatSourceGenerator(identity), null);
        }

        // Known generated document that failed content reconciliation must not be labeled Handwritten.
        if (session.Solution.GetDocument(tree) is SourceGeneratedDocument)
        {
            return (null, new CompilationUnavailableError(
                "A source-generated document could not be reconciled to a generator identity via GeneratorDriver.",
                "Retry after workspace_status is ready; if this persists, check analyzer/generator references."));
        }

        // FilePath is never enough on its own (ADR-0001 §6 / Spike S1).
        return (SymbolOrigin.Handwritten, null);
    }

    public async Task<(PagedResult<DiagnosticItem>? Success, SymbolQueryError? Error)> GetProjectDiagnosticsAsync(
        IWorkspaceSession session,
        string projectId,
        int? limit = null,
        string? cursor = null,
        TimeSpan? softBudget = null,
        CancellationToken cancellationToken = default)
    {
        var project = session.Solution.Projects
            .Where(p => OwnsProject(p))
            .FirstOrDefault(p =>
                string.Equals(p.Id.Id.ToString("D"), projectId, StringComparison.OrdinalIgnoreCase));

        if (project is null)
        {
            return (null, new ProjectNotFoundError(
                $"No project with projectId '{projectId}' is in the ready workspace.",
                "Call workspace_list_projects for valid projectId values, then retry project_diagnostics."));
        }

        var epoch = session.Epoch;
        var pageLimit = ClampLimit(limit);
        if (!SoftBudgetPage.TryReadOffset(
                cursor,
                epoch,
                "project_diagnostics",
                out var offset,
                out var cursorError))
        {
            return (null, cursorError);
        }

        var budget = softBudget ?? _softBudgets.SingleProjectCompile;
        Compilation? compilation;
        using (var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            if (budget <= TimeSpan.Zero)
            {
                budgetCts.Cancel();
            }
            else
            {
                budgetCts.CancelAfter(budget);
            }

            try
            {
                compilation = await session.GetCompilationAsync(project.Id, budgetCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (SoftBudgetPage.Finish(
                    Array.Empty<DiagnosticItem>(),
                    moreItems: false,
                    budgetHit: true,
                    () => MemberPageCursor.Encode(epoch, offset),
                    "project_diagnostics",
                    "Project has no error or warning diagnostics."), null);
            }
            catch (InvalidOperationException ex)
            {
                return (null, new CompilationUnavailableError(
                    ex.Message,
                    "Retry project_diagnostics; if it keeps failing, call workspace_list_projects and confirm the projectId."));
            }
        }

        if (compilation is null)
        {
            return (null, new CompilationUnavailableError(
                $"Compilation is unavailable for project '{project.Name}'.",
                "Retry project_diagnostics; if it keeps failing, call workspace_list_projects and confirm the projectId."));
        }

        var projectIdString = project.Id.Id.ToString("D");
        var diagnostics = compilation.GetDiagnostics(cancellationToken)
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(d => ToDiagnosticItem(d, projectIdString))
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .ThenBy(d => d.FilePath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(d => d.StartLine ?? -1)
            .ThenBy(d => d.StartCharacter ?? -1)
            .ThenBy(d => d.Message, StringComparer.Ordinal)
            .ToList();

        return SoftBudgetPage.Page(
            diagnostics,
            epoch,
            budgetHit: false,
            cursor,
            pageLimit,
            "project_diagnostics",
            "Project has no error or warning diagnostics.",
            "Diagnostics page complete.",
            "the diagnostics list");
    }

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

        if (!SymbolHandle.TryParse(handle, out var parsed, out var parseError) || parsed is null)
        {
            return (null, new InvalidSymbolHandleError(
                parseError ?? "Handle format or checksum is invalid.",
                "Call symbol_resolve with a name/FQN to obtain a fresh SymbolHandle; do not invent handles."));
        }

        if (!OwnsLanguage(parsed.Language))
        {
            return (null, new InvalidSymbolHandleError(
                $"Unsupported language '{parsed.Language}'.",
                "Call symbol_resolve for a C# or VB symbol to obtain a csharp or vb handle."));
        }

        var (project, symbol, resolveError) = await TryResolveHandleAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (resolveError is not null)
        {
            return (null, resolveError);
        }

        var (attribution, attrError) = await GetAttributionAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (attrError is not null)
        {
            return (null, attrError);
        }

        if (attribution!.Attribution.OriginKind == SymbolOrigin.SourceGenerator)
        {
            return (null, new GeneratedSymbolRenameRefusedError(
                "SourceGenerator declarations cannot be renamed.",
                "Change the generator input (handwritten partial / attribute) and call symbol_preview_rename on that symbol."));
        }

        if (string.Equals(symbol!.Name, newName, StringComparison.Ordinal))
        {
            return (null, new InvalidRenameNameError(
                $"New name '{newName}' is identical to the current symbol name.",
                "Pass a different identifier as newName."));
        }

        var renamed = await Renamer.RenameSymbolAsync(
            session.Solution,
            symbol,
            RenamePreviewService.DefaultOptions,
            newName,
            cancellationToken).ConfigureAwait(false);

        var (slices, generated) = await HandwrittenDocumentDiff
            .FromSolutionsAsync(session.Solution, renamed, cancellationToken)
            .ConfigureAwait(false);
        if (generated && slices.Count == 0)
        {
            return (null, new GeneratedSymbolRenameRefusedError(
                "This rename would only change generated documents.",
                "Change the generator input (handwritten partial / attribute) and call symbol_preview_rename on that symbol."));
        }

        var invalidated = new List<string> { handle };
        if (symbol is INamedTypeSymbol type)
        {
            foreach (var member in type.GetMembers().Where(static m => !m.IsImplicitlyDeclared))
            {
                if (member.Kind is SymbolKind.NamedType or SymbolKind.Namespace)
                {
                    continue;
                }

                var memberHandle = SymbolHandle.Create(
                    parsed.Language,
                    parsed.ProjectId,
                    member.ToDisplayString(SymbolDisplayFormats.SignatureQualified)).Format();
                if (!invalidated.Contains(memberHandle, StringComparer.Ordinal))
                {
                    invalidated.Add(memberHandle);
                }
            }
        }

        return (new RenamePreviewDraft(handle, newName, slices, invalidated), null);
    }

    private static DiagnosticItem ToDiagnosticItem(Diagnostic diagnostic, string projectId)
    {
        string? filePath = null;
        int? startLine = null;
        int? startCharacter = null;
        int? endLine = null;
        int? endCharacter = null;

        var location = diagnostic.Location;
        if (location.IsInSource)
        {
            var span = location.GetLineSpan();
            filePath = span.Path;
            startLine = span.StartLinePosition.Line + 1;
            startCharacter = span.StartLinePosition.Character;
            endLine = span.EndLinePosition.Line + 1;
            endCharacter = span.EndLinePosition.Character;
        }

        return new DiagnosticItem(
            Id: diagnostic.Id,
            Severity: diagnostic.Severity.ToString(),
            Message: diagnostic.GetMessage(),
            FilePath: filePath,
            StartLine: startLine,
            StartCharacter: startCharacter,
            EndLine: endLine,
            EndCharacter: endCharacter,
            ProjectId: projectId);
    }

    public Task<(Project? Project, ISymbol? Symbol, SymbolQueryError? Error)> ResolveHandleAsync(
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken = default) =>
        TryResolveHandleAsync(session, handle, cancellationToken);

    private async Task<(Project? Project, ISymbol? Symbol, SymbolQueryError? Error)> TryResolveHandleAsync(
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken)
    {
        if (!SymbolHandle.TryParse(handle, out var parsed, out var parseError) || parsed is null)
        {
            return (null, null, new InvalidSymbolHandleError(
                parseError ?? "Handle format or checksum is invalid.",
                "Call symbol_resolve with a name/FQN to obtain a fresh SymbolHandle; do not invent handles."));
        }

        if (!IsSupportedLanguageToken(parsed.Language))
        {
            return (null, null, new InvalidSymbolHandleError(
                $"Unsupported language '{parsed.Language}'.",
                "Call symbol_resolve for a C# or VB symbol to obtain a csharp or vb handle."));
        }

        var project = session.Solution.Projects.FirstOrDefault(p =>
            string.Equals(p.Id.Id.ToString("D"), parsed.ProjectId, StringComparison.OrdinalIgnoreCase));
        if (project is null)
        {
            return (null, null, new SymbolNotFoundError(
                $"Project '{parsed.ProjectId}' from the handle is not in the ready workspace.",
                "Confirm the workspace/project, then call symbol_resolve to obtain a new handle."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        Compilation compilation;
        try
        {
            compilation = await session.GetCompilationAsync(project.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return (null, null, new SymbolNotFoundError(
                $"Compilation for project '{parsed.ProjectId}' is unavailable.",
                "Call workspace_status; when ready, call symbol_resolve again."));
        }

        var symbol = FindBySignature(compilation, parsed.SignatureQualifiedName);
        if (symbol is null)
        {
            return (null, null, new SymbolNotFoundError(
                $"Symbol '{parsed.SignatureQualifiedName}' no longer exists in project '{parsed.ProjectId}'.",
                "The code may have changed; call symbol_resolve with the current name/FQN."));
        }

        return (project, symbol, null);
    }

    private static int ClampLimit(int? limit)
    {
        if (limit is null or <= 0)
        {
            return DefaultMemberPageLimit;
        }

        return Math.Min(limit.Value, MaxMemberPageLimit);
    }

    private static string SymbolKey(ISymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormats.SignatureQualified);

    private static SymbolResolveSuccess ToSuccess(Project project, ISymbol symbol)
    {
        var projectId = project.Id.Id.ToString("D");
        var language = LanguageToken(project.Language);
        var signature = symbol.ToDisplayString(SymbolDisplayFormats.SignatureQualified);
        var handle = SymbolHandle.Create(language, projectId, signature);
        var summary = new SymbolSummary(
            Kind: symbol.Kind.ToString(),
            DisplayName: symbol.ToDisplayString(SymbolDisplayFormats.ShortName),
            ContainingSymbol: symbol.ContainingSymbol is null ||
                              symbol.ContainingSymbol is INamespaceSymbol { IsGlobalNamespace: true }
                ? null
                : symbol.ContainingSymbol.ToDisplayString(SymbolDisplayFormats.SignatureQualified),
            Accessibility: symbol.DeclaredAccessibility.ToString(),
            ProjectId: projectId,
            Language: language,
            InteropKind: DetectInteropKind(symbol));

        return new SymbolResolveSuccess(handle.Format(), summary);
    }

    internal static string DetectInteropKind(ISymbol symbol)
    {
        if (symbol is not INamedTypeSymbol type)
        {
            return InteropKinds.None;
        }

        if (type.IsComImport || HasInteropAttribute(type, "System.Runtime.InteropServices.ComImportAttribute"))
        {
            return InteropKinds.ComImport;
        }

        if (HasInteropAttribute(type, "System.Runtime.InteropServices.TypeIdentifierAttribute"))
        {
            return InteropKinds.ComInteropWrapper;
        }

        var assembly = type.ContainingAssembly;
        if (assembly is not null)
        {
            foreach (var attr in assembly.GetAttributes())
            {
                var name = attr.AttributeClass?.ToDisplayString() ?? attr.AttributeClass?.Name;
                if (name is "System.Runtime.InteropServices.ImportedFromTypeLibAttribute"
                    or "ImportedFromTypeLibAttribute"
                    or "System.Runtime.InteropServices.PrimaryInteropAssemblyAttribute"
                    or "PrimaryInteropAssemblyAttribute")
                {
                    return InteropKinds.ComInteropWrapper;
                }
            }
        }

        return InteropKinds.None;
    }

    private static bool HasInteropAttribute(ISymbol symbol, string metadataName) =>
        symbol.GetAttributes().Any(a =>
            string.Equals(a.AttributeClass?.ToDisplayString(), metadataName, StringComparison.Ordinal) ||
            string.Equals(a.AttributeClass?.Name, metadataName.Split('.')[^1], StringComparison.Ordinal));

    public static string LanguageToken(string roslynLanguage) => roslynLanguage switch
    {
        LanguageNames.CSharp => CSharpLanguage,
        LanguageNames.VisualBasic => VbLanguage,
        LanguageNames.FSharp => FSharpLanguage,
        var other => other.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant()
    };

    public static bool IsSupportedLanguageToken(string token) =>
        string.Equals(token, CSharpLanguage, StringComparison.Ordinal) ||
        string.Equals(token, VbLanguage, StringComparison.Ordinal);

    public static bool IsSupportedRoslynLanguage(string roslynLanguage) =>
        roslynLanguage is LanguageNames.CSharp or LanguageNames.VisualBasic;


    private async Task CollectResolveMatchesAsync(
        IWorkspaceSession session,
        IReadOnlyList<Project> projects,
        string query,
        List<(Project Project, ISymbol Symbol)> matches,
        CancellationToken cancellationToken)
    {
        var cache = session as IWorkspaceSessionCaches;
        var remaining = new List<Project>();
        foreach (var project in projects)
        {
            if (cache is not null && cache.CompilationCache.TryGet(project.Id, out var warm))
            {
                foreach (var symbol in FindSymbols(warm, query))
                {
                    matches.Add((project, symbol));
                }
            }
            else
            {
                remaining.Add(project);
            }
        }

        if (TryFinishResolve(matches))
        {
            return;
        }

        var segment = query.Split('.', 2)[0];
        var named = remaining
            .Where(p => ProjectNameLooksLike(p, segment) && !IsTestLikeProject(p.Name))
            .ToList();
        var rest = remaining.Except(named).Where(p => !IsTestLikeProject(p.Name)).ToList();

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(_softBudgets.SingleProjectCompile);
        try
        {
            foreach (var project in named.Concat(rest))
            {
                budgetCts.Token.ThrowIfCancellationRequested();
                await AddResolveMatchesAsync(session, project, query, matches, budgetCts.Token)
                    .ConfigureAwait(false);
                if (TryFinishResolve(matches))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task AddResolveMatchesAsync(
        IWorkspaceSession session,
        Project project,
        string query,
        List<(Project Project, ISymbol Symbol)> matches,
        CancellationToken cancellationToken)
    {
        Compilation compilation;
        try
        {
            compilation = await session.GetCompilationAsync(project.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        foreach (var symbol in FindSymbols(compilation, query))
        {
            matches.Add((project, symbol));
        }
    }

    private static bool TryFinishResolve(List<(Project Project, ISymbol Symbol)> matches)
    {
        if (matches.Count == 0)
        {
            return false;
        }

        var sourceDefining = matches
            .Where(m => m.Symbol.Locations.Any(static l => l.IsInSource))
            .DistinctBy(m => (m.Project.Id.Id, SymbolKey(m.Symbol)))
            .ToList();
        return sourceDefining.Count == 1;
    }

    private static bool ProjectNameLooksLike(Project project, string segment) =>
        project.Name.Contains(segment, StringComparison.OrdinalIgnoreCase) ||
        (project.DefaultNamespace?.Contains(segment, StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool IsTestLikeProject(string name) =>
        name.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Bench", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Dummy", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<Project> FilterProjects(
        Solution solution,
        string? projectId,
        out SymbolQueryError? error)
    {
        error = null;
        var supported = solution.Projects
            .Where(p => IsSupportedRoslynLanguage(p.Language))
            .ToArray();

        if (string.IsNullOrWhiteSpace(projectId))
        {
            return supported;
        }

        var match = supported
            .Where(p => string.Equals(p.Id.Id.ToString("D"), projectId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (match.Length == 0)
        {
            error = new SymbolNotFoundError(
                $"No project with projectId '{projectId}' is in the ready workspace.",
                "Call workspace_list_projects for valid projectId values, then retry symbol_resolve.");
        }

        return match;
    }

    private static IEnumerable<ISymbol> FindSymbols(Compilation compilation, string query)
    {
        var byMetadata = compilation.GetTypeByMetadataName(query);
        if (byMetadata is not null)
        {
            yield return byMetadata;
            yield break;
        }

        foreach (var type in EnumerateAllTypes(compilation.GlobalNamespace))
        {
            var signature = type.ToDisplayString(SymbolDisplayFormats.SignatureQualified);
            var metadataStyle = type.ContainingNamespace is { IsGlobalNamespace: false } ns
                ? $"{ns.ToDisplayString()}.{type.Name}"
                : type.Name;

            if (string.Equals(signature, query, StringComparison.Ordinal) ||
                string.Equals(metadataStyle, query, StringComparison.Ordinal) ||
                string.Equals(type.Name, query, StringComparison.Ordinal))
            {
                yield return type;
                continue;
            }

            foreach (var member in type.GetMembers())
            {
                if (member.Kind is SymbolKind.NamedType or SymbolKind.Namespace)
                {
                    continue;
                }

                var memberSig = member.ToDisplayString(SymbolDisplayFormats.SignatureQualified);
                if (string.Equals(memberSig, query, StringComparison.Ordinal) ||
                    string.Equals(member.Name, query, StringComparison.Ordinal) ||
                    string.Equals($"{metadataStyle}.{member.Name}", query, StringComparison.Ordinal))
                {
                    yield return member;
                }
            }
        }
    }

    private static ISymbol? FindBySignature(Compilation compilation, string signatureQualifiedName)
    {
        foreach (var type in EnumerateAllTypes(compilation.GlobalNamespace))
        {
            if (string.Equals(
                    type.ToDisplayString(SymbolDisplayFormats.SignatureQualified),
                    signatureQualifiedName,
                    StringComparison.Ordinal))
            {
                return type;
            }

            foreach (var member in type.GetMembers())
            {
                if (member.Kind is SymbolKind.NamedType or SymbolKind.Namespace)
                {
                    continue;
                }

                if (string.Equals(
                        member.ToDisplayString(SymbolDisplayFormats.SignatureQualified),
                        signatureQualifiedName,
                        StringComparison.Ordinal))
                {
                    return member;
                }
            }
        }

        return null;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateAllTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            foreach (var t in EnumerateTypeAndNested(type))
            {
                yield return t;
            }
        }

        foreach (var child in ns.GetNamespaceMembers())
        {
            foreach (var t in EnumerateAllTypes(child))
            {
                yield return t;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypeAndNested(INamedTypeSymbol type)
    {
        yield return type;
        foreach (var nested in type.GetTypeMembers())
        {
            foreach (var t in EnumerateTypeAndNested(nested))
            {
                yield return t;
            }
        }
    }
}
