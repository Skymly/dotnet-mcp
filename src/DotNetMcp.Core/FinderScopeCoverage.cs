using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
/// its documents contain — and the engine does not say so. The page must name
/// those projects instead of claiming a completeness the search could not
/// deliver.
/// </summary>
/// <remarks>
/// A declared <see cref="ProjectReference"/> edge is not enough on its own
/// (issue #565): MSBuildWorkspace materializes the edge as a
/// <see cref="PortableExecutableReference"/> to the target's output dll, so a
/// missing output leaves the dependent's compilation with no assembly symbol
/// for the defining assembly — calls cannot bind and the search finds nothing
/// even though the edge was declared. The gate therefore verifies that a
/// declared edge actually materialized a resolvable reference to the
/// origination's assembly name before counting the project as searched.
/// A <see cref="ProjectReference"/> the loader could not resolve may leave
/// no graph edge at all (issue #570), so the gap descriptor also reads the
/// declared items from the dependent's own project file before folding it
/// into the collapsed count. Scoped projects with no usable reference of any
/// kind can never contain hits, so they collapse to a count instead of
/// individual names.
/// </remarks>
public static class FinderScopeCoverage
{
    /// <summary>
    /// A declared-scope project the finder could not search, with why.
    /// <see cref="Collapsed"/> marks a project with no usable reference at
    /// all — it is counted rather than named in the page message.
    /// </summary>
    public sealed record UnsearchedProject(
        string ProjectId,
        string Name,
        string Reason,
        bool Collapsed = false);

    /// <summary>
    /// Projects inside <paramref name="scopeDocuments"/> whose documents the
    /// Roslyn finder gate skips for <paramref name="symbol"/>. Mirrors
    /// DependentProjectsFinder: source originations admit a project when it is
    /// an originating project itself or holds a direct
    /// <see cref="ProjectReference"/> to one <em>that materialized a resolvable
    /// reference to the origination's assembly</em>; metadata originations
    /// admit a project when a <see cref="PortableExecutableReference"/>
    /// resolves to the defining assembly name. Originations follow the
    /// engine's initial search set — the symbol, its source definition and
    /// linked-document twins (multi-targeted/shared files), and the up-cascade
    /// (overridden and implemented-interface members). The engine's
    /// per-project down-cascade runs after the searched set is fixed, so it
    /// cannot widen coverage and is not mirrored. Internal-visibility
    /// originations additionally require InternalsVisibleTo access, matching
    /// the engine's visibility filter.
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

        // Declared-edge dependents of any source origination, direct or
        // transitive. A scoped project on the declared chain could plausibly
        // contain hits (its sources were written against the defining
        // assembly), so it is named rather than collapsed even when no usable
        // reference materialized at the end of the chain.
        var transitiveDependents = new HashSet<ProjectId>();
        var dependencyGraph = solution.GetProjectDependencyGraph();
        foreach (var origination in originations)
        {
            if (origination.SourceProjectId is null)
            {
                continue;
            }

            foreach (var dependentId in dependencyGraph
                         .GetProjectsThatTransitivelyDependOnThisProject(origination.SourceProjectId))
            {
                transitiveDependents.Add(dependentId);
            }
        }

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

            unsearched.Add(await DescribeGapAsync(
                    solution,
                    project,
                    originations,
                    assemblyNames,
                    transitiveDependents,
                    searchedName,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        return unsearched;
    }

    /// <summary>
    /// Text appended to (or replacing) the page message so a covered page never
    /// reads as fully searched (issue #559). Names every plausibly affected
    /// project with its reason, matching the F# adapter's "were not searched"
    /// disclosure, and collapses projects with no usable reference into a
    /// count — they could never contain hits (issue #565).
    /// </summary>
    public static string DescribeUnsearched(IReadOnlyList<UnsearchedProject> unsearched)
    {
        var parts = unsearched
            .Where(static u => !u.Collapsed)
            .Select(static u => $"'{u.Name}' ({u.Reason})")
            .ToList();
        var collapsedGroups = unsearched
            .Where(static u => u.Collapsed)
            .GroupBy(static u => u.Reason, StringComparer.Ordinal)
            .OrderBy(static g => g.Key, StringComparer.Ordinal);
        foreach (var group in collapsedGroups)
        {
            var noun = group.Count() == 1 ? "scoped project" : "scoped projects";
            var prefix = parts.Count > 0
                ? $"and {group.Count()} other {noun}"
                : $"{group.Count()} {noun}";
            parts.Add($"{prefix} with {group.Key}");
        }

        return string.Join("; ", parts);
    }

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
            bool hasReference;
            if (origination.SourceProjectId is not null)
            {
                // The originating project is always a dependent of its own
                // origination — the engine marks it searched with
                // hasInternalsAccess: true, so the visibility gate does not
                // apply to it either.
                if (project.Id == origination.SourceProjectId)
                {
                    return true;
                }

                // A declared ProjectReference edge is only a candidate: the
                // edge must also have materialized a resolvable reference to
                // the origination's assembly inside this compilation, or the
                // project contributes zero hits (issue #565).
                hasReference = candidateIds.Contains(origination.SourceProjectId) &&
                    await ResolvesToAssemblyAsync(
                            project, origination.AssemblyName, cancellationToken)
                        .ConfigureAwait(false);
            }
            else
            {
                hasReference = await ReferencesAssemblyByNameAsync(
                        project, origination.AssemblyName, cancellationToken)
                    .ConfigureAwait(false);
            }

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
    /// Why the gate skips <paramref name="project"/>: a declared project
    /// reference to an origination that materialized no usable reference (the
    /// "project reference without matching metadata reference" load failure),
    /// a <c>&lt;ProjectReference&gt;</c> the loader dropped entirely so the
    /// declared edge exists only in the project file, an assembly-name
    /// reference that exists only as metadata/package content, a project edge
    /// that lands on a different load of the defining assembly, a transitive
    /// declared chain with no usable reference at the end, or no usable
    /// reference at all — the only shape that collapses to a count instead of
    /// a name.
    /// </summary>
    private static async Task<UnsearchedProject> DescribeGapAsync(
        Solution solution,
        Project project,
        IReadOnlyList<Origination> originations,
        HashSet<string> assemblyNames,
        HashSet<ProjectId> transitiveDependents,
        string searchedName,
        CancellationToken cancellationToken)
    {
        var candidateIds = project.ProjectReferences
            .Select(static r => r.ProjectId)
            .ToHashSet();

        // A declared edge to an origination that reached this branch did not
        // admit the project: either the edge produced no resolvable assembly
        // symbol, or the internal-visibility gate dropped it.
        foreach (var origination in originations)
        {
            if (origination.SourceProjectId is null ||
                !candidateIds.Contains(origination.SourceProjectId))
            {
                continue;
            }

            var targetName = solution.GetProject(origination.SourceProjectId)?.Name
                ?? origination.AssemblyName;
            if (!await ResolvesToAssemblyAsync(
                        project, origination.AssemblyName, cancellationToken)
                    .ConfigureAwait(false))
            {
                return Gap(
                    $"declared project reference to '{targetName}' did not materialize (missing output/metadata)");
            }

            if (origination.MinVisibilityRank == InternalRank &&
                !origination.InternalsVisibleTo.Contains(project.AssemblyName ?? string.Empty))
            {
                return Gap($"declared project reference to '{targetName}' but lacks InternalsVisibleTo access");
            }
        }

        // A <ProjectReference> the loader could not resolve leaves no graph
        // edge at all (or only an edge to a synthesized ProjectId that maps to
        // no loaded project): the engine's exact-ProjectId dependent gate
        // skips the project, and every reference-based check above also fails.
        // The declared intent survives only in the project file itself —
        // reading it keeps the folded count from swallowing a project that
        // asked for the dependency (issue #570: Reactive.Tests collapsed this
        // way on a multi-TFM solution).
        var originationPaths = originations
            .Select(static o => o.FilePath)
            .Where(static p => p is not null)
            .Select(static p => Path.GetFullPath(p!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var declaredPath = await DeclaredOriginationPathAsync(
                solution, project, originationPaths, cancellationToken)
            .ConfigureAwait(false);
        if (declaredPath is not null)
        {
            return Gap(
                $"declared project reference to '{Path.GetFileNameWithoutExtension(declaredPath)}' did not materialize in the loaded workspace");
        }

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
            return Gap($"references '{searchedName}' only via metadata/package reference, not a project reference");
        }

        var alternateUnit = project.ProjectReferences
            .Select(r => solution.GetProject(r.ProjectId))
            .Where(p => p is not null)
            .FirstOrDefault(p => assemblyNames.Contains(p!.AssemblyName ?? string.Empty));
        if (alternateUnit is not null)
        {
            return Gap($"references '{searchedName}' through '{alternateUnit.Name}', a different load of the assembly");
        }

        if (transitiveDependents.Contains(project.Id))
        {
            return Gap(
                $"references '{searchedName}' only through a declared project-reference chain (no usable reference materialized)");
        }

        return new UnsearchedProject(
            project.Id.Id.ToString("D"),
            project.Name,
            $"no usable reference to '{searchedName}'",
            Collapsed: true);

        UnsearchedProject Gap(string reason) =>
            new(project.Id.Id.ToString("D"), project.Name, reason);
    }

    /// <summary>
    /// Whether <paramref name="project"/>'s compilation holds any reference
    /// that resolves to an assembly named <paramref name="assemblyName"/> —
    /// <see cref="PortableExecutableReference"/>s (MSBuildWorkspace output
    /// paths) and <see cref="CompilationReference"/>s (declared project edges)
    /// alike, both resolved through the same API. A declared edge that
    /// produced no resolvable assembly symbol — a missing output dll, an
    /// unresolved path, a target that cannot emit a metadata reference —
    /// returns <see langword="false"/>.
    /// </summary>
    private static async Task<bool> ResolvesToAssemblyAsync(
        Project project,
        string assemblyName,
        CancellationToken cancellationToken)
    {
        var compilation = await project.GetCompilationAsync(cancellationToken)
            .ConfigureAwait(false);
        if (compilation is null)
        {
            return false;
        }

        foreach (var reference in compilation.References)
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
                        FilePath: null,
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
                            project.FilePath,
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

    /// <summary>
    /// Matches a <c>&lt;ProjectReference Include="…"&gt;</c> item in a project
    /// file; the <c>include</c> group carries the declared path.
    /// </summary>
    private static readonly Regex ProjectReferenceInclude = new(
        "<ProjectReference\\b[^>]*\\bInclude\\s*=\\s*[\"'](?<include>[^\"']+)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The normalized path of a <c>&lt;ProjectReference&gt;</c> item the
    /// project file declares to an origination unit's project file but that
    /// never became a usable graph edge — the loader drops an unresolvable
    /// reference entirely or leaves an edge whose synthesized
    /// <see cref="ProjectId"/> resolves to no project. Returns
    /// <see langword="null"/> when the project file is missing/unreadable,
    /// when no declaration matches, or when a loaded edge already lands on
    /// the origination's project file (the edge materialized — it points at
    /// a different unit, which the "different load" check describes).
    /// </summary>
    private static async Task<string?> DeclaredOriginationPathAsync(
        Solution solution,
        Project project,
        HashSet<string> originationPaths,
        CancellationToken cancellationToken)
    {
        if (project.FilePath is null || originationPaths.Count == 0)
        {
            return null;
        }

        foreach (var reference in project.ProjectReferences)
        {
            var target = solution.GetProject(reference.ProjectId);
            if (target?.FilePath is { } targetPath &&
                originationPaths.Contains(Path.GetFullPath(targetPath)))
            {
                return null;
            }
        }

        string contents;
        try
        {
            if (!File.Exists(project.FilePath))
            {
                return null;
            }

            contents = await File.ReadAllTextAsync(project.FilePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        var projectDirectory = Path.GetDirectoryName(project.FilePath);
        if (projectDirectory is null)
        {
            return null;
        }

        foreach (Match match in ProjectReferenceInclude.Matches(contents))
        {
            if (match.Value.Contains("ReferenceOutputAssembly=\"false\"", StringComparison.OrdinalIgnoreCase) ||
                match.Value.Contains("ReferenceOutputAssembly='false'", StringComparison.OrdinalIgnoreCase))
            {
                // Analyzer/generator edges never materialize an assembly
                // reference by design — not a declaration gap.
                continue;
            }

            var include = match.Groups["include"].Value;
            if (include.Contains('$'))
            {
                continue; // Property-expanded paths cannot be evaluated here.
            }

            var normalized = include
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            var declared = Path.GetFullPath(Path.Combine(projectDirectory, normalized));
            if (originationPaths.Contains(declared))
            {
                return declared;
            }
        }

        return null;
    }

    private sealed record Origination(
        string AssemblyName,
        ProjectId? SourceProjectId,
        string? FilePath,
        int MinVisibilityRank,
        HashSet<string> InternalsVisibleTo);
}
