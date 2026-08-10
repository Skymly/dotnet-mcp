using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Core;

public sealed class SymbolQueryService
{
    public const string CSharpLanguage = "csharp";
    public const int DefaultMemberPageLimit = 50;
    public const int MaxMemberPageLimit = 100;
    public static readonly TimeSpan DependencyClosureSoftBudget = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan EntireSolutionSoftBudget = TimeSpan.FromSeconds(20);

    private readonly GeneratorQueryService _generators;

    public SymbolQueryService(GeneratorQueryService generators)
    {
        _generators = generators;
    }

    public async Task<(SymbolResolveSuccess? Success, SymbolQueryError? Error)> ResolveByNameAsync(
        Solution solution,
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
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
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
        Solution solution,
        string handle,
        CancellationToken cancellationToken = default)
    {
        var (project, symbol, error) = await TryResolveHandleAsync(solution, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        return (ToSuccess(project!, symbol!), null);
    }

    public async Task<(SymbolDefinitionSuccess? Success, SymbolQueryError? Error)> GetDefinitionAsync(
        Solution solution,
        string handle,
        long epoch,
        CancellationToken cancellationToken = default)
    {
        var (project, symbol, error) = await TryResolveHandleAsync(solution, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        var locations = new List<SymbolLocation>();
        foreach (var location in symbol!.Locations)
        {
            var (mapped, mapError) = await ToSymbolLocationAsync(
                    solution, project!, epoch, location, cancellationToken)
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
        Solution solution,
        string handle,
        long epoch,
        CancellationToken cancellationToken = default)
    {
        var (project, symbol, error) = await TryResolveHandleAsync(solution, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        var (attribution, attrError) = await AttributeSymbolAsync(
                solution, project!, epoch, symbol!, cancellationToken)
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
                        solution, project!, epoch, member, cancellationToken)
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
        Solution solution,
        string handle,
        long epoch,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var (project, symbol, error) = await TryResolveHandleAsync(solution, handle, cancellationToken)
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

    public async Task<(PagedResult<ReferenceLocationItem>? Success, SymbolQueryError? Error)> FindReferencesAsync(
        Solution solution,
        string handle,
        long epoch,
        bool entireSolution = false,
        int? limit = null,
        string? cursor = null,
        TimeSpan? softBudget = null,
        CancellationToken cancellationToken = default)
    {
        var (project, symbol, error) = await TryResolveHandleAsync(solution, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        var pageLimit = ClampLimit(limit);
        var budget = softBudget ?? (entireSolution ? EntireSolutionSoftBudget : DependencyClosureSoftBudget);
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
                    solution, doc, referenced, epoch, cancellationToken)
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

    private async Task<IReadOnlyList<ReferenceLocationItem>> FlattenReferenceHitsForDocumentAsync(
        Solution solution,
        Document document,
        IEnumerable<Microsoft.CodeAnalysis.FindSymbols.ReferencedSymbol> referencedSymbols,
        long epoch,
        CancellationToken cancellationToken)
    {
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
                        solution,
                        document,
                        epoch,
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
                        solution,
                        document,
                        epoch,
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
        Solution solution,
        Document document,
        long epoch,
        Location location,
        string kind,
        CancellationToken cancellationToken)
    {
        var (mapped, error) = await ToSymbolLocationAsync(
                solution, document.Project, epoch, location, cancellationToken)
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
        Solution solution,
        Project project,
        long epoch,
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
                solution,
                project,
                epoch,
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
        Solution solution,
        Project project,
        long epoch,
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
        var (origin, originError) = await ResolveOriginAsync(solution, project, epoch, tree, cancellationToken)
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
        Solution solution,
        Project project,
        long epoch,
        SyntaxTree? tree,
        CancellationToken cancellationToken)
    {
        if (tree is null)
        {
            return (SymbolOrigin.Handwritten, null);
        }

        var projectId = project.Id.Id.ToString("D");
        var (identity, matchError) = await _generators
            .MatchSyntaxTreeAsync(solution, projectId, epoch, tree, cancellationToken)
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
        if (solution.GetDocument(tree) is SourceGeneratedDocument)
        {
            return (null, new CompilationUnavailableError(
                "A source-generated document could not be reconciled to a generator identity via GeneratorDriver.",
                "Retry after workspace_status is ready; if this persists, check analyzer/generator references."));
        }

        // FilePath is never enough on its own (ADR-0001 §6 / Spike S1).
        return (SymbolOrigin.Handwritten, null);
    }

    private async Task<(Project? Project, ISymbol? Symbol, SymbolQueryError? Error)> TryResolveHandleAsync(
        Solution solution,
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

        var project = solution.Projects.FirstOrDefault(p =>
            string.Equals(p.Id.Id.ToString("D"), parsed.ProjectId, StringComparison.OrdinalIgnoreCase));
        if (project is null)
        {
            return (null, null, new SymbolNotFoundError(
                $"Project '{parsed.ProjectId}' from the handle is not in the ready workspace.",
                "Confirm the workspace/project, then call symbol_resolve to obtain a new handle."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null)
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
