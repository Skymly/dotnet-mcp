using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace DotNetMcp.Core;

/// <summary>
/// Declared-scope versus searched coverage for the document-based finders
/// (issue #559). <c>SymbolFinder</c> gates which projects it opens through
/// Roslyn's dependent-projects logic: for a source-originated symbol only the
/// originating projects and their <em>direct</em> project-reference dependents
/// are searched; for a metadata-originated symbol only projects holding a
/// <see cref="PortableExecutableReference"/> to the defining assembly name. A
/// scoped project outside that gate contributes zero hits regardless of what
/// its documents contain — a project reference whose output never materialized
/// degrades the same way — and the engine does not say so. The page must name
/// those projects instead of claiming a completeness the search could not
/// deliver.
/// </summary>
public static class FinderScopeCoverage
{
    /// <summary>A declared-scope project the finder could not search, with why.</summary>
    public sealed record UnsearchedProject(string ProjectId, string Name, string Reason);

    /// <summary>
    /// Projects inside <paramref name="scopeDocuments"/> whose documents the
    /// Roslyn finder gate skips for <paramref name="symbol"/>. Mirrors
    /// DependentProjectsFinder: source originations admit a project when it is
    /// an originating project itself or holds a direct
    /// <see cref="ProjectReference"/> to one; metadata originations admit a
    /// project when a <see cref="PortableExecutableReference"/> resolves to the
    /// defining assembly name. Originations follow the engine's initial search
    /// set — the symbol, its source definition and linked-document twins
    /// (multi-targeted/shared files), and the up-cascade (overridden and
    /// implemented-interface members). The engine's per-project down-cascade
    /// runs after the searched set is fixed, so it cannot widen coverage and is
    /// not mirrored. Internal-visibility originations additionally require
    /// InternalsVisibleTo access, matching the engine's visibility filter.
    /// </summary>
    public static async Task<IReadOnlyList<UnsearchedProject>> UnsearchedProjectsAsync(
        Solution solution,
        ISymbol symbol,
        IReadOnlyList<Document> scopeDocuments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(scopeDocuments);

        var scopeProjects = scopeDocuments
            .Select(static d => d.Project)
            .DistinctBy(static p => p.Id)
            .OrderBy(static p => p.Name, StringComparer.Ordinal)
            .ThenBy(static p => p.Id.Id)
            .ToList();
        if (scopeProjects.Count == 0)
        {
            return [];
        }

        var originations = await OriginationsAsync(solution, symbol, cancellationToken)
            .ConfigureAwait(false);
        if (originations.Count == 0)
        {
            return [];
        }

        var assemblyNames = originations
            .Select(static o => o.AssemblyName)
            .ToHashSet(StringComparer.Ordinal);
        var searchedName = symbol.OriginalDefinition?.ContainingAssembly?.Name
            ?? originations[0].AssemblyName;

        var unsearched = new List<UnsearchedProject>();
        foreach (var project in scopeProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!project.SupportsCompilation)
            {
                unsearched.Add(new UnsearchedProject(
                    project.Id.Id.ToString("D"),
                    project.Name,
                    $"{project.Language} project is not searched for a C#/VB symbol"));
                continue;
            }

            if (await IsSearchedAsync(project, originations, cancellationToken)
                    .ConfigureAwait(false))
            {
                continue;
            }

            unsearched.Add(new UnsearchedProject(
                project.Id.Id.ToString("D"),
                project.Name,
                await DescribeGapAsync(
                        solution, project, assemblyNames, searchedName, cancellationToken)
                    .ConfigureAwait(false)));
        }

        return unsearched;
    }

    /// <summary>
    /// Text appended to (or replacing) the page message so a covered page never
    /// reads as fully searched (issue #559). Names every affected project with
    /// its reason, matching the F# adapter's "were not searched" disclosure.
    /// </summary>
    public static string DescribeUnsearched(IReadOnlyList<UnsearchedProject> unsearched) =>
        string.Join("; ", unsearched.Select(static u => $"'{u.Name}' ({u.Reason})"));

    private static async Task<bool> IsSearchedAsync(
        Project project,
        IReadOnlyList<Origination> originations,
        CancellationToken cancellationToken)
    {
        var candidateIds = project.ProjectReferences
            .Select(static r => r.ProjectId)
            .ToHashSet();
        foreach (var origination in originations)
        {
            // The originating project is always a dependent of its own
            // origination; other source-origination dependents need a direct
            // ProjectReference edge to it.
            var hasReference = origination.SourceProjectId is not null
                ? project.Id == origination.SourceProjectId ||
                  candidateIds.Contains(origination.SourceProjectId)
                : await ReferencesAssemblyByNameAsync(
                        project, origination.AssemblyName, cancellationToken)
                    .ConfigureAwait(false);
            if (!hasReference)
            {
                continue;
            }

            // Internal-visibility searches drop dependents without
            // InternalsVisibleTo access.
            if (origination.MinVisibilityRank != InternalRank ||
                origination.InternalsVisibleTo.Contains(project.AssemblyName ?? string.Empty))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Why the gate skips <paramref name="project"/>: an assembly-name
    /// reference that exists only as metadata/package content, a project edge
    /// that lands on a different load of the defining assembly, or no usable
    /// reference at all (the "project reference without matching metadata
    /// reference" load failure, issue #559).
    /// </summary>
    private static async Task<string> DescribeGapAsync(
        Solution solution,
        Project project,
        HashSet<string> assemblyNames,
        string searchedName,
        CancellationToken cancellationToken)
    {
        var metadataOnly = false;
        foreach (var name in assemblyNames)
        {
            if (await ReferencesAssemblyByNameAsync(project, name, cancellationToken)
                    .ConfigureAwait(false))
            {
                metadataOnly = true;
                searchedName = name;
                break;
            }
        }

        if (metadataOnly)
        {
            return $"references '{searchedName}' only via metadata/package reference, not a project reference";
        }

        var alternateUnit = project.ProjectReferences
            .Select(r => solution.GetProject(r.ProjectId))
            .Where(p => p is not null)
            .FirstOrDefault(p => assemblyNames.Contains(p!.AssemblyName ?? string.Empty));
        if (alternateUnit is not null)
        {
            return $"references '{searchedName}' through '{alternateUnit.Name}', a different load of the assembly";
        }

        return $"no usable reference to '{searchedName}' (missing project/metadata reference)";
    }

    /// <summary>
    /// Mirrors <c>HasReferenceToAssemblyAsync</c>: only
    /// <see cref="PortableExecutableReference"/> counts, matched by resolved
    /// assembly name — a <see cref="CompilationReference"/> or an unresolved
    /// path is not a metadata reference to the assembly.
    /// </summary>
    private static async Task<bool> ReferencesAssemblyByNameAsync(
        Project project,
        string assemblyName,
        CancellationToken cancellationToken)
    {
        var peReferences = project.MetadataReferences
            .OfType<PortableExecutableReference>()
            .ToList();
        if (peReferences.Count == 0)
        {
            return false;
        }

        var compilation = await project.GetCompilationAsync(cancellationToken)
            .ConfigureAwait(false);
        if (compilation is null)
        {
            return false;
        }

        foreach (var reference in peReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly &&
                string.Equals(assembly.Name, assemblyName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Originating assemblies the way the engine's initial search set sees them:
    /// the symbol's source definition, its linked-document projects
    /// (multi-targeted/shared files — each twin project is its own origination),
    /// and the up-cascade (overridden and implemented-interface members).
    /// <c>FindLinkedSymbolsAsync</c>/<c>GetOriginatingProject</c> are internal,
    /// so the linked twin set is recovered through each declaring document's
    /// linked document ids — equivalent for origination purposes.
    /// </summary>
    private static async Task<IReadOnlyList<Origination>> OriginationsAsync(
        Solution solution,
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        var sourceOriginations = new Dictionary<ProjectId, Origination>();
        var metadataOriginations = new Dictionary<string, Origination>(StringComparer.Ordinal);
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var workQueue = new Queue<ISymbol>();
        workQueue.Enqueue(symbol);
        while (workQueue.TryDequeue(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mapped = await SymbolFinder
                .FindSourceDefinitionAsync(current, solution, cancellationToken)
                .ConfigureAwait(false) ?? current;
            if (!seen.Add(mapped))
            {
                continue;
            }

            var linkedProjects = await LinkedProjectsAsync(solution, mapped, cancellationToken)
                .ConfigureAwait(false);
            if (linkedProjects.Count == 0)
            {
                var assembly = mapped.OriginalDefinition?.ContainingAssembly
                    ?? mapped.ContainingAssembly;
                if (assembly is not null &&
                    metadataOriginations.TryGetValue(assembly.Name, out var existing))
                {
                    metadataOriginations[assembly.Name] = existing with
                    {
                        MinVisibilityRank = Math.Min(
                            existing.MinVisibilityRank, EffectiveVisibilityRank(mapped))
                    };
                }
                else if (assembly is not null)
                {
                    metadataOriginations[assembly.Name] = new Origination(
                        assembly.Name,
                        SourceProjectId: null,
                        EffectiveVisibilityRank(mapped),
                        InternalsVisibleToSet(assembly));
                }
            }
            else
            {
                foreach (var (project, compilation) in linkedProjects)
                {
                    var internalsVisibleTo = compilation?.Assembly is { } projectAssembly
                        ? InternalsVisibleToSet(projectAssembly)
                        : [];
                    var rank = EffectiveVisibilityRank(mapped);
                    if (sourceOriginations.TryGetValue(project.Id, out var existing))
                    {
                        sourceOriginations[project.Id] = existing with
                        {
                            MinVisibilityRank = Math.Min(existing.MinVisibilityRank, rank)
                        };
                    }
                    else
                    {
                        sourceOriginations[project.Id] = new Origination(
                            project.AssemblyName ?? mapped.ContainingAssembly?.Name ?? string.Empty,
                            project.Id,
                            rank,
                            internalsVisibleTo);
                    }
                }
            }

            foreach (var up in await UpCascadeAsync(solution, mapped, cancellationToken)
                         .ConfigureAwait(false))
            {
                if (!seen.Contains(up))
                {
                    workQueue.Enqueue(up);
                }
            }
        }

        return [.. sourceOriginations.Values, .. metadataOriginations.Values];
    }

    /// <summary>
    /// The projects that host a declaration of <paramref name="symbol"/> — its
    /// declaring documents plus every document linked to them (TFM/shared-file
    /// twins). A source symbol with no declaring document in the solution
    /// yields an empty list and the symbol is treated as metadata-originated.
    /// </summary>
    private static async Task<IReadOnlyList<(Project Project, Compilation? Compilation)>> LinkedProjectsAsync(
        Solution solution,
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        var projects = new List<(Project, Compilation?)>();
        var seen = new HashSet<ProjectId>();
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var document = solution.GetDocument(reference.SyntaxTree);
            if (document is null)
            {
                continue;
            }

            foreach (var documentId in document.GetLinkedDocumentIds().Append(document.Id))
            {
                var project = solution.GetProject(documentId.ProjectId);
                if (project is null || !project.SupportsCompilation || !seen.Add(project.Id))
                {
                    continue;
                }

                var compilation = await project.GetCompilationAsync(cancellationToken)
                    .ConfigureAwait(false);
                projects.Add((project, compilation));
            }
        }

        return projects;
    }

    /// <summary>
    /// The up-cascade the engine applies before fixing the searched set:
    /// overridden members, explicit interface implementations, and — only for
    /// source symbols whose containing type sits under interfaces — the
    /// implemented interface members.
    /// </summary>
    private static async Task<IEnumerable<ISymbol>> UpCascadeAsync(
        Solution solution,
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        var up = new List<ISymbol>();
        switch (symbol)
        {
            case IMethodSymbol method:
                if (method.OverriddenMethod is { } overriddenMethod)
                {
                    up.Add(overriddenMethod);
                }

                up.AddRange(method.ExplicitInterfaceImplementations);
                break;
            case IPropertySymbol property:
                if (property.OverriddenProperty is { } overriddenProperty)
                {
                    up.Add(overriddenProperty);
                }

                up.AddRange(property.ExplicitInterfaceImplementations);
                break;
            case IEventSymbol eventSymbol:
                if (eventSymbol.OverriddenEvent is { } overriddenEvent)
                {
                    up.Add(overriddenEvent);
                }

                up.AddRange(eventSymbol.ExplicitInterfaceImplementations);
                break;
            case INamedTypeSymbol named:
                if (named.BaseType is { } baseType)
                {
                    up.Add(baseType);
                }

                up.AddRange(named.AllInterfaces);
                break;
        }

        var containingType = symbol.ContainingType;
        if (symbol is not INamedTypeSymbol &&
            containingType?.AllInterfaces.Length > 0 &&
            symbol.DeclaringSyntaxReferences.Length > 0)
        {
            up.AddRange(await SymbolFinder
                .FindImplementedInterfaceMembersAsync(symbol, solution, null, cancellationToken)
                .ConfigureAwait(false));
        }

        return up;
    }

    private const int PrivateRank = 0;
    private const int InternalRank = 1;
    private const int PublicRank = 2;

    /// <summary>
    /// Mirrors the engine's min-visibility filter: the most restrictive
    /// accessibility on the symbol-or-container chain wins, and only an
    /// Internal result makes dependents require InternalsVisibleTo access.
    /// Private and protected-only declarations stay unfiltered, matching the
    /// engine — their callers can only live inside the declaring project
    /// anyway.
    /// </summary>
    private static int EffectiveVisibilityRank(ISymbol symbol)
    {
        var rank = PublicRank;
        for (var current = symbol; current is not null; current = current.ContainingSymbol)
        {
            rank = current.DeclaredAccessibility switch
            {
                Accessibility.Internal or Accessibility.ProtectedOrInternal =>
                    Math.Min(rank, InternalRank),
                Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal =>
                    Math.Min(rank, PrivateRank),
                _ => rank,
            };
        }

        return rank;
    }

    private static HashSet<string> InternalsVisibleToSet(IAssemblySymbol assembly)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in assembly.GetAttributes())
        {
            if (!string.Equals(
                    attribute.AttributeClass?.Name,
                    nameof(InternalsVisibleToAttribute),
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (attribute.ConstructorArguments.FirstOrDefault().Value is not string name)
            {
                continue;
            }

            var comma = name.IndexOf(',');
            set.Add((comma >= 0 ? name[..comma] : name).Trim());
        }

        return set;
    }

    private sealed record Origination(
        string AssemblyName,
        ProjectId? SourceProjectId,
        int MinVisibilityRank,
        HashSet<string> InternalsVisibleTo);
}
