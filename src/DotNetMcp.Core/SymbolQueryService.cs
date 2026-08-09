using Microsoft.CodeAnalysis;

namespace DotNetMcp.Core;

public sealed class SymbolQueryService
{
    public const string CSharpLanguage = "csharp";
    public const int DefaultMemberPageLimit = 50;
    public const int MaxMemberPageLimit = 100;

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
        CancellationToken cancellationToken = default)
    {
        var (project, symbol, error) = await TryResolveHandleAsync(solution, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        _ = project;
        var locations = new List<SymbolLocation>();
        foreach (var location in symbol!.Locations)
        {
            locations.Add(ToSymbolLocation(solution, location));
        }

        if (locations.Count == 0)
        {
            return (null, new DefinitionNotFoundError(
                $"No definition locations were found for '{symbol.ToDisplayString(SymbolDisplayFormats.SignatureQualified)}'.",
                "Confirm the handle with symbol_summary, or call symbol_resolve for a source symbol."));
        }

        return (new SymbolDefinitionSuccess(locations), null);
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

    private static SymbolLocation ToSymbolLocation(Solution solution, Location location)
    {
        if (location.IsInMetadata)
        {
            return new SymbolLocation(
                DeclarationAvailability.InMetadata,
                Origin: null,
                FilePath: null,
                Start: null,
                Length: null);
        }

        if (!location.IsInSource)
        {
            return new SymbolLocation(
                DeclarationAvailability.None,
                Origin: null,
                FilePath: null,
                Start: null,
                Length: null);
        }

        var tree = location.SourceTree;
        var span = location.SourceSpan;
        var path = tree?.FilePath;
        var origin = ResolveOrigin(solution, tree, path);

        return new SymbolLocation(
            DeclarationAvailability.InSource,
            origin,
            path,
            span.Start,
            span.Length);
    }

    private static string ResolveOrigin(Solution solution, SyntaxTree? tree, string? filePath)
    {
        if (tree is not null && solution.GetDocument(tree) is SourceGeneratedDocument)
        {
            return SymbolOrigin.SourceGenerated;
        }

        // Heuristic until full generator identity (#14/#15): keep generated trees visible and labeled.
        if (filePath is not null &&
            (filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
             filePath.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
             filePath.Contains($"{Path.DirectorySeparatorChar}Generated{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
             filePath.Contains("/Generated/", StringComparison.Ordinal)))
        {
            return SymbolOrigin.SourceGenerated;
        }

        return SymbolOrigin.Handwritten;
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
