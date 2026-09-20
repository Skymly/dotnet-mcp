using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;

namespace DotNetMcp.Core;

public sealed partial class RoslynLanguageAdapter : ILanguageAdapter
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
    public static readonly SymbolRenameOptions DefaultRenameOptions = new(
        RenameOverloads: false,
        RenameInStrings: false,
        RenameInComments: false,
        RenameFile: false);

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
        IsSupportedLanguageToken(languageToken);

    public bool OwnsProject(Project project) =>
        IsSupportedRoslynLanguage(project.Language);

    public bool SupportsCodeRefactoring => true;

    public bool SupportsDiagnosticFix => true;

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
        var timedOut = false;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            timedOut = await CollectResolveMatchesAsync(session, projects, query, matches, cancellationToken)
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
            if (timedOut)
            {
                return (null, new SoftBudgetExceededError(
                    $"Timed out while resolving '{name}' before any match was found.",
                    "Pass projectId from workspace_list_projects, or retry symbol_resolve."));
            }

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

        if (timedOut)
        {
            return (null, new SoftBudgetExceededError(
                $"Timed out while resolving '{name}' after finding a candidate; remaining projects were not scanned.",
                "Pass projectId from workspace_list_projects, or retry symbol_resolve."));
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
            found = FindInstancePropertyOrField(current, name, publicOnly);
            if (found is not null)
            {
                break;
            }
        }

        if (found is null)
        {
            foreach (var iface in type.AllInterfaces)
            {
                found = FindInstancePropertyOrField(iface, name, publicOnly);
                if (found is not null)
                {
                    break;
                }
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
            handle,
            "Type has no listable members.",
            "Member page complete.",
            "the member list");
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

        var generatedDocument = session.Solution.GetDocument(tree) is SourceGeneratedDocument;
        var projectId = project.Id.Id.ToString("D");
        var (identity, matchError) = await _generators
            .MatchSyntaxTreeAsync(session, projectId, tree, cancellationToken)
            .ConfigureAwait(false);

        if (generatedDocument)
        {
            if (identity is not null)
            {
                return (SymbolOrigin.FormatSourceGenerator(identity), null);
            }

            if (matchError is not null)
            {
                return (null, matchError);
            }

            return (null, new CompilationUnavailableError(
                "A source-generated document could not be reconciled to a generator identity via GeneratorDriver.",
                "Retry after workspace_status is ready; if this persists, check analyzer/generator references."));
        }

        if (identity is not null)
        {
            return (SymbolOrigin.FormatSourceGenerator(identity), null);
        }

        // Handwritten trees stay Handwritten even when the generator driver fails.
        return (SymbolOrigin.Handwritten, null);
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


    private static ISymbol? FindInstancePropertyOrField(ITypeSymbol type, string name, bool publicOnly)
    {
        var candidates = type.GetMembers(name)
            .Where(m => !m.IsStatic && !m.IsImplicitlyDeclared)
            .Where(m => !publicOnly || m.DeclaredAccessibility == Accessibility.Public)
            .ToArray();

        return candidates.OfType<IPropertySymbol>().FirstOrDefault(p => p.Parameters.Length == 0)
            ?? (ISymbol?)candidates.OfType<IFieldSymbol>().FirstOrDefault();
    }

    public static bool IsSupportedRoslynLanguage(string roslynLanguage) =>
        roslynLanguage is LanguageNames.CSharp or LanguageNames.VisualBasic;


    private async Task<bool> CollectResolveMatchesAsync(
        IWorkspaceSession session,
        IReadOnlyList<Project> projects,
        string query,
        List<(Project Project, ISymbol Symbol)> matches,
        CancellationToken cancellationToken)
    {
        var cache = session as IWorkspaceSessionCaches;
        var warmMain = new List<Project>();
        var warmTestLike = new List<Project>();
        var remainingMain = new List<Project>();
        var remainingTestLike = new List<Project>();
        foreach (var project in projects)
        {
            var testLike = IsTestLikeProject(project.Name);
            if (cache is not null && cache.CompilationCache.TryGet(project.Id, out _))
            {
                (testLike ? warmTestLike : warmMain).Add(project);
            }
            else
            {
                (testLike ? remainingTestLike : remainingMain).Add(project);
            }
        }

        AddWarmResolveMatches(cache, warmMain, query, matches);

        var segment = query.Split('.', 2)[0];
        var named = remainingMain.Where(p => ProjectNameLooksLike(p, segment)).ToList();
        var rest = remainingMain.Except(named).ToList();

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_softBudgets.SingleProjectCompile <= TimeSpan.Zero)
        {
            budgetCts.Cancel();
        }
        else
        {
            budgetCts.CancelAfter(_softBudgets.SingleProjectCompile);
        }

        try
        {
            foreach (var project in named.Concat(rest))
            {
                budgetCts.Token.ThrowIfCancellationRequested();
                await AddResolveMatchesAsync(session, project, query, matches, budgetCts.Token)
                    .ConfigureAwait(false);
            }

            AddWarmResolveMatches(cache, warmTestLike, query, matches);

            foreach (var project in remainingTestLike)
            {
                budgetCts.Token.ThrowIfCancellationRequested();
                await AddResolveMatchesAsync(session, project, query, matches, budgetCts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return true;
        }

        return false;
    }

    private static void AddWarmResolveMatches(
        IWorkspaceSessionCaches? cache,
        IEnumerable<Project> projects,
        string query,
        List<(Project Project, ISymbol Symbol)> matches)
    {
        if (cache is null)
        {
            return;
        }

        foreach (var project in projects)
        {
            if (!cache.CompilationCache.TryGet(project.Id, out var warm))
            {
                continue;
            }

            foreach (var symbol in FindSymbols(warm, query))
            {
                matches.Add((project, symbol));
            }
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

    private static bool ProjectNameLooksLike(Project project, string segment) =>
        project.Name.Contains(segment, StringComparison.OrdinalIgnoreCase) ||
        (project.DefaultNamespace?.Contains(segment, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Test/bench/dummy projects are searched after other projects when <c>projectId</c> is omitted.
    /// Match whole name segments (<c>Foo.Tests</c>, <c>Foo.Test</c>, <c>Foo.Benches</c>), not substrings
    /// like <c>Contest</c> / <c>Latest</c> / <c>Attest</c>. Warm and cold compilation paths share this rule.
    /// </summary>
    internal static bool IsTestLikeProject(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (var segment in name.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (segment.Equals("Test", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Tests", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Bench", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Benches", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Dummy", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

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
