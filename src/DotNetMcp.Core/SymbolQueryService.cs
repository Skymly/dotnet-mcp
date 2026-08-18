using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace DotNetMcp.Core;

public sealed class SymbolQueryService
{
    public const string CSharpLanguage = "csharp";
    public const int DefaultMemberPageLimit = 50;
    public const int MaxMemberPageLimit = 100;
    public static readonly TimeSpan DependencyClosureSoftBudget =
        SoftBudgetOptions.Default.FindRefsScoped;
    public static readonly TimeSpan EntireSolutionSoftBudget =
        SoftBudgetOptions.Default.FindRefsEntireSolution;

    private readonly GeneratorQueryService _generators;
    private readonly SoftBudgetOptions _softBudgets;

    public SymbolQueryService(
        GeneratorQueryService generators,
        SoftBudgetOptions? softBudgets = null)
    {
        _generators = generators;
        _softBudgets = softBudgets ?? SoftBudgetOptions.Default;
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

        var solution = session.Solution;
        var projects = FilterProjects(solution, projectId, out var projectFilterError);
        if (projectFilterError is not null)
        {
            return (null, projectFilterError);
        }

        var query = name.Trim();
        var matches = new List<(Project Project, ISymbol Symbol)>();
        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Compilation? compilation;
            try
            {
                compilation = await session.GetCompilationAsync(project.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            foreach (var symbol in FindSymbols(compilation, query))
            {
                matches.Add((project, symbol));
            }
        }

        if (matches.Count == 0)
        {
            return (null, new SymbolNotFoundError(
                $"No symbol named '{name}' was found in the ready workspace.",
                "Confirm the name/FQN (and optional projectId), then call symbol_resolve again."));
        }

        // Deduplicate identical symbols within the same project (walk may hit twice).
        matches = matches
            .DistinctBy(m => (m.Project.Id.Id, SymbolKey(m.Symbol)))
            .ToList();

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

        return LookupTypeMember(project!, type, memberName);
    }

    /// <summary>
    /// Continue a Binding-path walk from an already-resolved type without a handle round-trip.
    /// </summary>
    internal (TypeMemberLookup? Success, SymbolQueryError? Error) LookupTypeMember(
        Project project,
        ITypeSymbol type,
        string memberName)
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
                .Where(m => m.DeclaredAccessibility == Accessibility.Public)
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
        var offset = 0;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!MemberPageCursor.TryDecode(cursor, out var cursorEpoch, out offset, out var cursorError))
            {
                return (null, new StaleCursorError(
                    cursorError ?? "Cursor is invalid.",
                    "Call symbol_members again without a cursor to start a fresh page."));
            }

            if (cursorEpoch != epoch)
            {
                return (null, new StaleCursorError(
                    $"Cursor epoch {cursorEpoch} does not match workspace epoch {epoch}.",
                    "Call symbol_members again without a cursor; do not retry with the stale cursor."));
            }
        }

        var members = type.GetMembers()
            .Where(m => m.Kind is not SymbolKind.NamedType and not SymbolKind.Namespace)
            .Where(m => !m.IsImplicitlyDeclared)
            .OrderBy(SymbolKey, StringComparer.Ordinal)
            .ToList();

        if (offset > members.Count)
        {
            return (null, new StaleCursorError(
                "Cursor offset is past the end of the member list.",
                "Call symbol_members again without a cursor to start a fresh page."));
        }

        var slice = members.Skip(offset).Take(pageLimit).ToList();
        var nextOffset = offset + slice.Count;
        var truncated = nextOffset < members.Count;
        string? nextCursor = truncated ? MemberPageCursor.Encode(epoch, nextOffset) : null;

        var items = slice.Select(m =>
        {
            var success = ToSuccess(project!, m);
            return new MemberListItem(success.Handle, success.Summary);
        }).ToList();

        var message = truncated
            ? "Results truncated; pass nextCursor to symbol_members to continue (do not restart from the first page)."
            : members.Count == 0
                ? "Type has no listable members."
                : "Member page complete.";

        return (new PagedResult<MemberListItem>(items, truncated, nextCursor, message), null);
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
        var offset = 0;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!MemberPageCursor.TryDecode(cursor, out var cursorEpoch, out offset, out var cursorError))
            {
                return (null, new StaleCursorError(
                    cursorError ?? "Cursor is invalid.",
                    "Call symbol_find_implementations again without a cursor to start a fresh page."));
            }

            if (cursorEpoch != epoch)
            {
                return (null, new StaleCursorError(
                    $"Cursor epoch {cursorEpoch} does not match workspace epoch {epoch}.",
                    "Call symbol_find_implementations again without a cursor; do not retry with the stale cursor."));
            }
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
            return (null, new StaleCursorError(
                "Cursor offset is past the end of the implementation list.",
                "Call symbol_find_implementations again without a cursor to start a fresh page."));
        }

        var slice = ordered.Skip(offset).Take(pageLimit).ToList();
        var nextOffset = offset + slice.Count;
        var truncated = nextOffset < ordered.Count;
        string? nextCursor = truncated ? MemberPageCursor.Encode(epoch, nextOffset) : null;

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

        var message = truncated
            ? "Results truncated; pass nextCursor to symbol_find_implementations to continue (do not restart from the first page)."
            : items.Count == 0
                ? "No implementations or derived types found."
                : "Implementation page complete.";

        return (new PagedResult<ImplementationItem>(items, truncated, nextCursor, message), null);
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
        var offset = 0;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!MemberPageCursor.TryDecode(cursor, out var cursorEpoch, out offset, out var cursorError))
            {
                return (null, new StaleCursorError(
                    cursorError ?? "Cursor is invalid.",
                    "Call symbol_type_hierarchy again without a cursor to start a fresh page."));
            }

            if (cursorEpoch != epoch)
            {
                return (null, new StaleCursorError(
                    $"Cursor epoch {cursorEpoch} does not match workspace epoch {epoch}.",
                    "Call symbol_type_hierarchy again without a cursor; do not retry with the stale cursor."));
            }
        }

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

        if (offset > chain.Count)
        {
            return (null, new StaleCursorError(
                "Cursor offset is past the end of the type hierarchy.",
                "Call symbol_type_hierarchy again without a cursor to start a fresh page."));
        }

        var slice = chain.Skip(offset).Take(pageLimit).ToList();
        var nextOffset = offset + slice.Count;
        var truncated = nextOffset < chain.Count;
        string? nextCursor = truncated ? MemberPageCursor.Encode(epoch, nextOffset) : null;

        var message = truncated
            ? "Results truncated; pass nextCursor to symbol_type_hierarchy to continue (do not restart from the first page)."
            : chain.Count == 0
                ? "Type has no base types or interfaces."
                : "Type hierarchy page complete.";

        return (new PagedResult<HierarchyItem>(slice, truncated, nextCursor, message), null);
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
        var epoch = session.Epoch;
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
        var budget = softBudget ?? _softBudgets.FindRefsEntireSolution;
        var docIndex = 0;
        var locOffset = 0;

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!FindRefsPageCursor.TryDecode(
                    cursor,
                    out var cursorEpoch,
                    out var cursorEntireSolution,
                    out docIndex,
                    out locOffset,
                    out var cursorError))
            {
                return (null, new StaleCursorError(
                    cursorError ?? "Cursor is invalid.",
                    "Call symbol_find_callers again without a cursor to start a fresh page."));
            }

            if (cursorEpoch != epoch || cursorEntireSolution)
            {
                return (null, new StaleCursorError(
                    cursorEpoch != epoch
                        ? $"Cursor epoch {cursorEpoch} does not match workspace epoch {epoch}."
                        : "Cursor payload is invalid.",
                    "Call symbol_find_callers again without a cursor; do not retry with the stale cursor."));
            }
        }

        var documents = solution.Projects
            .SelectMany(p => p.Documents)
            .OrderBy(d => d.Project.Name, StringComparer.Ordinal)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ThenBy(d => d.Id.Id)
            .ToList();

        if (docIndex > documents.Count || (docIndex == documents.Count && locOffset > 0))
        {
            return (null, new StaleCursorError(
                "Cursor document index is past the end of the document list.",
                "Call symbol_find_callers again without a cursor to start a fresh page."));
        }

        var page = new List<CallerLocationItem>();
        var stopwatch = Stopwatch.StartNew();
        var truncatedByBudget = false;
        var nextDocIndex = documents.Count;
        var nextLocOffset = 0;
        var exhausted = true;

        for (var i = docIndex; i < documents.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (page.Count > 0 && stopwatch.Elapsed >= budget)
            {
                truncatedByBudget = true;
                exhausted = false;
                nextDocIndex = i;
                nextLocOffset = 0;
                break;
            }

            var doc = documents[i];
            IEnumerable<SymbolCallerInfo> callers;
            using (var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var remaining = budget - stopwatch.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    budgetCts.CancelAfter(remaining);
                }

                try
                {
                    callers = await SymbolFinder
                        .FindCallersAsync(
                            symbol!,
                            solution,
                            ImmutableHashSet.Create(doc),
                            budgetCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    truncatedByBudget = true;
                    exhausted = false;
                    nextDocIndex = i;
                    nextLocOffset = 0;
                    break;
                }
            }

            var hits = (await FlattenCallerHitsForDocumentAsync(
                    session, doc, callers, cancellationToken)
                .ConfigureAwait(false))
                .OrderBy(h => h.FilePath ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(h => h.Start ?? -1)
                .ThenBy(h => h.Length ?? -1)
                .ThenBy(h => h.CallerHandle, StringComparer.Ordinal)
                .ToList();

            var startLoc = i == docIndex ? locOffset : 0;
            if (startLoc > hits.Count)
            {
                return (null, new StaleCursorError(
                    "Cursor location offset is past the end of hits for a document.",
                    "Call symbol_find_callers again without a cursor to start a fresh page."));
            }

            for (var loc = startLoc; loc < hits.Count; loc++)
            {
                if (page.Count >= pageLimit)
                {
                    exhausted = false;
                    nextDocIndex = i;
                    nextLocOffset = loc;
                    break;
                }

                page.Add(hits[loc]);
            }

            if (!exhausted)
            {
                break;
            }
        }

        var truncated = !exhausted;
        string? nextCursor = truncated
            ? FindRefsPageCursor.Encode(epoch, entireSolution: false, nextDocIndex, nextLocOffset)
            : null;

        var message = truncated
            ? truncatedByBudget
                ? $"Soft budget reached after {page.Count} item(s). Pass nextCursor to continue; do not retry from scratch."
                : "Results truncated; pass nextCursor to symbol_find_callers to continue (do not restart from the first page)."
            : page.Count == 0
                ? "No direct callers found."
                : "Caller page complete.";

        return (new PagedResult<CallerLocationItem>(page, truncated, nextCursor, message), null);
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
        var epoch = session.Epoch;
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
        var docIndex = 0;
        var locOffset = 0;

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!FindRefsPageCursor.TryDecode(
                    cursor,
                    out var cursorEpoch,
                    out var cursorEntireSolution,
                    out docIndex,
                    out locOffset,
                    out var cursorError))
            {
                return (null, new StaleCursorError(
                    cursorError ?? "Cursor is invalid.",
                    "Call symbol_find_references again without a cursor to start a fresh page."));
            }

            if (cursorEpoch != epoch)
            {
                return (null, new StaleCursorError(
                    $"Cursor epoch {cursorEpoch} does not match workspace epoch {epoch}.",
                    "Call symbol_find_references again without a cursor; do not retry with the stale cursor."));
            }

            if (cursorEntireSolution != entireSolution)
            {
                return (null, new StaleCursorError(
                    "Cursor scope does not match the entireSolution parameter for this request.",
                    "Call symbol_find_references again without a cursor; do not retry with the stale cursor."));
            }
        }

        var scope = entireSolution
            ? FindRefsScopeKind.EntireSolution
            : FindRefsScopeKind.DependencyClosure;
        var documents = FindRefsScopes.DocumentsForScope(solution, project!, scope)
            .OrderBy(d => d.Project.Name, StringComparer.Ordinal)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ThenBy(d => d.Id.Id)
            .ToList();

        if (docIndex > documents.Count || (docIndex == documents.Count && locOffset > 0))
        {
            return (null, new StaleCursorError(
                "Cursor document index is past the end of the scoped document list.",
                "Call symbol_find_references again without a cursor to start a fresh page."));
        }

        var page = new List<ReferenceLocationItem>();
        var stopwatch = Stopwatch.StartNew();
        var truncatedByBudget = false;
        var nextDocIndex = documents.Count;
        var nextLocOffset = 0;
        var exhausted = true;

        for (var i = docIndex; i < documents.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Soft budget: stop before the next document once we already have results.
            if (page.Count > 0 && stopwatch.Elapsed >= budget)
            {
                truncatedByBudget = true;
                exhausted = false;
                nextDocIndex = i;
                nextLocOffset = 0;
                break;
            }

            var doc = documents[i];
            IEnumerable<Microsoft.CodeAnalysis.FindSymbols.ReferencedSymbol> referenced;
            using (var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var remaining = budget - stopwatch.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    budgetCts.CancelAfter(remaining);
                }

                try
                {
                    referenced = await FindRefsScopes
                        .FindReferencesInDocumentsAsync(
                            symbol!,
                            solution,
                            ImmutableHashSet.Create(doc),
                            budgetCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Soft budget cancelled mid-document; next page gets a fresh budget from this doc.
                    truncatedByBudget = true;
                    exhausted = false;
                    nextDocIndex = i;
                    nextLocOffset = 0;
                    break;
                }
            }

            var hits = (await FlattenReferenceHitsForDocumentAsync(
                    session, doc, referenced, cancellationToken)
                .ConfigureAwait(false))
                .OrderBy(h => h.FilePath ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(h => h.Start ?? -1)
                .ThenBy(h => h.Length ?? -1)
                .ThenBy(h => h.Kind, StringComparer.Ordinal)
                .ToList();

            var startLoc = i == docIndex ? locOffset : 0;
            if (startLoc > hits.Count)
            {
                return (null, new StaleCursorError(
                    "Cursor location offset is past the end of hits for a document.",
                    "Call symbol_find_references again without a cursor to start a fresh page."));
            }

            for (var loc = startLoc; loc < hits.Count; loc++)
            {
                if (page.Count >= pageLimit)
                {
                    exhausted = false;
                    nextDocIndex = i;
                    nextLocOffset = loc;
                    break;
                }

                page.Add(hits[loc]);
            }

            if (!exhausted)
            {
                break;
            }
        }

        var truncated = !exhausted;
        string? nextCursor = truncated
            ? FindRefsPageCursor.Encode(epoch, entireSolution, nextDocIndex, nextLocOffset)
            : null;

        var message = truncated
            ? truncatedByBudget
                ? $"Soft budget reached after {page.Count} item(s). Pass nextCursor to continue; do not retry from scratch."
                : "Results truncated; pass nextCursor to symbol_find_references to continue (do not restart from the first page)."
            : page.Count == 0
                ? "No references found in the selected scope."
                : "Reference page complete.";

        return (new PagedResult<ReferenceLocationItem>(page, truncated, nextCursor, message), null);
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

        if (!string.Equals(parsed.Language, CSharpLanguage, StringComparison.Ordinal))
        {
            return (null, null, new InvalidSymbolHandleError(
                $"Unsupported language '{parsed.Language}'.",
                "Call symbol_resolve for a C# symbol to obtain a csharp handle."));
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
        var signature = symbol.ToDisplayString(SymbolDisplayFormats.SignatureQualified);
        var handle = SymbolHandle.Create(CSharpLanguage, projectId, signature);
        var summary = new SymbolSummary(
            Kind: symbol.Kind.ToString(),
            DisplayName: symbol.ToDisplayString(SymbolDisplayFormats.ShortName),
            ContainingSymbol: symbol.ContainingSymbol is null ||
                              symbol.ContainingSymbol is INamespaceSymbol { IsGlobalNamespace: true }
                ? null
                : symbol.ContainingSymbol.ToDisplayString(SymbolDisplayFormats.SignatureQualified),
            Accessibility: symbol.DeclaredAccessibility.ToString(),
            ProjectId: projectId,
            Language: CSharpLanguage);

        return new SymbolResolveSuccess(handle.Format(), summary);
    }

    private static IReadOnlyList<Project> FilterProjects(
        Solution solution,
        string? projectId,
        out SymbolQueryError? error)
    {
        error = null;
        var csharp = solution.Projects
            .Where(p => p.Language == LanguageNames.CSharp)
            .ToArray();

        if (string.IsNullOrWhiteSpace(projectId))
        {
            return csharp;
        }

        var match = csharp
            .Where(p => string.Equals(p.Id.Id.ToString("D"), projectId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (match.Length == 0)
        {
            error = new SymbolNotFoundError(
                $"No C# project with projectId '{projectId}' is in the ready workspace.",
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
