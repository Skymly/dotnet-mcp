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
/// Being inside the searched set is still not a hit guarantee (issue #572):
/// Roslyn does not count an OverloadResolutionFailure candidate as a
/// FindCallers/FindReferences hit, and a dependent whose declared references
/// never materialized binds exactly those — an unrestored project loses every
/// declared <c>&lt;PackageReference&gt;</c> without even an unresolved stub,
/// so the materialized-edge test can pass while the project's call sites
/// silently produce nothing. The fidelity signal itself reads the
/// compilation snapshot, not the live file system (issue #577): a declared
/// package reference only counts when a
/// <see cref="PortableExecutableReference"/> under a NuGet package-cache
/// root materialized in the same compilation the hits were searched
/// against, so a restore/build in flight — <c>project.assets.json</c>
/// already on disk while the snapshot still lacks the packages — no longer
/// opens a false-pass window. A declared <c>&lt;Reference&gt;</c> path that
/// never materialized degrades the same way. Projects failing that
/// structural fidelity check are named alongside the unsearched list with a
/// searched-degraded reason.
/// </remarks>
public static class FinderScopeCoverage
{
    /// <summary>
    /// A declared-scope project the finder could not search — or searched
    /// without the references needed to produce hits (issue #572) — with
    /// why. <see cref="Collapsed"/> marks a project with no usable reference
    /// at all — it is counted rather than named in the page message.
    /// Searched-degraded projects are never collapsed: they are the shape
    /// most likely to be quietly dropping hits.
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
    /// the engine's visibility filter. A project the gate admits also goes
    /// through the searched-degraded fidelity check (issues #572/#577):
    /// declared references absent from the loaded compilation — a declared
    /// <c>&lt;PackageReference&gt;</c> with no
    /// <see cref="PortableExecutableReference"/> under a NuGet package-cache
    /// root, a declared <c>&lt;Reference&gt;</c> path that never
    /// materialized, or a compilation holding no
    /// <see cref="PortableExecutableReference"/> at all — mean its hits may
    /// be incomplete even though the engine searched it, so it is named with
    /// a searched-degraded reason rather than passing silently.
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

            var (searched, compilation) = await IsSearchedAsync(
                    project, originations, cancellationToken)
                .ConfigureAwait(false);
            if (searched)
            {
                // Searched is not the same as able to produce hits (issue
                // #572): a project whose declared references never
                // materialized binds OverloadResolutionFailure candidates the
                // finders do not count, so name it searched-degraded.
                var degraded = await SearchedDegradedReasonAsync(
                        project, compilation, cancellationToken)
                    .ConfigureAwait(false);
                if (degraded is not null)
                {
                    unsearched.Add(new UnsearchedProject(
                        project.Id.Id.ToString("D"), project.Name, degraded));
                }

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
    /// disclosure — searched-degraded projects (issue #572) join the named
    /// list with their own reason — and collapses projects with no usable
    /// reference into a count — they could never contain hits (issue #565).
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

    /// <summary>
    /// The engine's searched verdict plus the compilation the gate already
    /// materialized while reaching it — handed to the searched-degraded
    /// fidelity check so a project is never asked for its compilation twice
    /// (<see cref="Project.GetCompilationAsync"/> caches, but the gate still
    /// keeps one fetch per project).
    /// </summary>
    private static async Task<(bool Searched, Compilation? Compilation)> IsSearchedAsync(
        Project project,
        IReadOnlyList<Origination> originations,
        CancellationToken cancellationToken)
    {
        var candidateIds = project.ProjectReferences
            .Select(static r => r.ProjectId)
            .ToHashSet();
        Compilation? compilation = null;
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
                    return (true, compilation);
                }

                // A declared ProjectReference edge is only a candidate: the
                // edge must also have materialized a resolvable reference to
                // the origination's assembly inside this compilation, or the
                // project contributes zero hits (issue #565).
                hasReference = candidateIds.Contains(origination.SourceProjectId) &&
                    ResolvesToAssembly(
                        compilation ??= await project
                            .GetCompilationAsync(cancellationToken)
                            .ConfigureAwait(false),
                        origination.AssemblyName,
                        cancellationToken);
            }
            else
            {
                var peReferences = project.MetadataReferences
                    .OfType<PortableExecutableReference>();
                hasReference = peReferences.Any() &&
                    ReferencesAssemblyByName(
                        compilation ??= await project
                            .GetCompilationAsync(cancellationToken)
                            .ConfigureAwait(false),
                        peReferences,
                        origination.AssemblyName,
                        cancellationToken);
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
                return (true, compilation);
            }
        }

        return (false, compilation);
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
        CancellationToken cancellationToken) =>
        ResolvesToAssembly(
            await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false),
            assemblyName,
            cancellationToken);

    private static bool ResolvesToAssembly(
        Compilation? compilation,
        string assemblyName,
        CancellationToken cancellationToken)
    {
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

        return ReferencesAssemblyByName(
            await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false),
            peReferences,
            assemblyName,
            cancellationToken);
    }

    private static bool ReferencesAssemblyByName(
        Compilation? compilation,
        IEnumerable<PortableExecutableReference> peReferences,
        string assemblyName,
        CancellationToken cancellationToken)
    {
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
    /// Issue #572/#577 fidelity check for a project the engine gate already
    /// counts as searched: "was searched" is not "can produce hits". The
    /// primary signal lives in the compilation snapshot itself — a declared
    /// <c>&lt;PackageReference&gt;</c> only counts as materialized when a
    /// <see cref="PortableExecutableReference"/> under a NuGet package-cache
    /// root resolved in the same compilation the finders searched, so a
    /// restore/build in flight cannot pass the check on a written
    /// <c>project.assets.json</c> the snapshot has not caught up with yet
    /// (framework ref-pack references under the dotnet install tree never
    /// count — a project holding only those is still degraded). The assets
    /// file on disk stays as evidence that chooses the wording, never the
    /// verdict. Secondary shapes: a compilation holding not even one
    /// <see cref="PortableExecutableReference"/> cannot bind a call site no
    /// matter what the declared items say, and a declared
    /// <c>&lt;Reference&gt;</c> file path (a <c>&lt;HintPath&gt;</c> or an
    /// <c>Include</c> that is itself a path) missing on disk never made it
    /// into the compilation either. The check stays structural — a
    /// declared-items regex over the project file plus the reference list
    /// the gate already materialized — never a semantic diagnostic pass.
    /// Returns the searched-degraded reason, or <see langword="null"/> when
    /// the project is whole enough to produce its hits. Projects declaring
    /// no <c>&lt;PackageReference&gt;</c> or <c>&lt;Reference&gt;</c> item at
    /// all (a pure traversal or project-reference-only file) depend on no
    /// restored output and are skipped.
    /// </summary>
    private static async Task<string?> SearchedDegradedReasonAsync(
        Project project,
        Compilation? compilation,
        CancellationToken cancellationToken)
    {
        if (project.FilePath is null)
        {
            return null;
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

        // Commented-out items are not declarations; match the declared-items
        // surface the same way MSBuild sees it.
        contents = XmlComment.Replace(contents, string.Empty);

        var declaresPackage = PackageReferenceInclude.IsMatch(contents);
        var declaresReference = ReferenceInclude.IsMatch(contents);
        if (!declaresPackage && !declaresReference)
        {
            return null;
        }

        var projectDirectory = Path.GetDirectoryName(project.FilePath);
        compilation ??= await project.GetCompilationAsync(cancellationToken)
            .ConfigureAwait(false);

        // Primary signal (issue #577): the compilation snapshot. The file
        // system can run ahead of the snapshot — dotnet restore writes
        // obj/project.assets.json before the loaded compilation picks the
        // packages up — so "assets.json exists" alone proved nothing. An
        // undeterminable package-cache root set fails conservative instead
        // of degrading a project the check cannot honestly judge.
        if (declaresPackage && compilation is not null)
        {
            var packageRoots = NuGetPackageRoots();
            if (packageRoots.Count > 0 &&
                !HoldsPackageCacheReference(compilation, packageRoots))
            {
                var assetsMissing = projectDirectory is null ||
                    !File.Exists(Path.Combine(
                        projectDirectory, "obj", "project.assets.json"));
                return assetsMissing
                    ? "was searched but its declared package references are absent from the loaded compilation " +
                        "(obj/project.assets.json missing); hits from it may be incomplete"
                    : "was searched but its declared package references are absent from the loaded compilation " +
                        "(stale snapshot; a restore may still be in progress); hits from it may be incomplete";
            }
        }

        // A compilation holding not even one PortableExecutableReference —
        // no framework assembly, nothing — cannot bind a call site no matter
        // what the declared items say (the extreme unrestored shape).
        if (compilation is not null &&
            compilation.References.All(static r => r is not PortableExecutableReference))
        {
            return "was searched but its declared references never materialized " +
                "(no assembly references resolved); hits from it may be incomplete";
        }

        // A declared <Reference> file path that does not exist — a missing
        // HintPath target or a missing declared dll — is the same gap one
        // level narrower: the rest of the compilation materialized, the one
        // declared input did not.
        if (declaresReference &&
            projectDirectory is not null &&
            MissingDeclaredReferencePath(contents, projectDirectory) is { } missing)
        {
            return $"was searched but a declared <Reference> did not materialize ('{missing}' not found); " +
                "hits from it may be incomplete";
        }

        return null;
    }

    /// <summary>
    /// The declared path of the first <c>&lt;Reference&gt;</c> item whose
    /// file is missing — its <c>&lt;HintPath&gt;</c> child when one is
    /// declared, else an <c>Include</c> that is itself a path (contains a
    /// directory separator or names a <c>.dll</c>, e.g.
    /// <c>SomeVendor.dll</c>). Bare assembly names carry no resolvable path —
    /// they bind through framework/GAC resolution the structural check
    /// cannot judge — and property-expanded or wildcard paths cannot be
    /// evaluated, so all three shapes are skipped. Returns
    /// <see langword="null"/> when every declared path exists or none can be
    /// resolved to a path at all.
    /// </summary>
    private static string? MissingDeclaredReferencePath(string contents, string projectDirectory)
    {
        foreach (Match match in ReferenceItem.Matches(contents))
        {
            var hint = match.Groups["body"].Success &&
                HintPathElement.Match(match.Groups["body"].Value) is { Success: true } hintMatch
                    ? hintMatch.Groups["hint"].Value.Trim()
                    : null;
            var include = match.Groups["include"].Value.Trim();
            var declared = !string.IsNullOrWhiteSpace(hint)
                ? hint
                : LooksLikeReferencePath(include) ? include : null;
            if (declared is null || declared.Contains('$') || declared.Contains('*'))
            {
                continue;
            }

            var normalized = declared
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(projectDirectory, normalized));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!File.Exists(full))
            {
                return declared;
            }
        }

        return null;
    }

    /// <summary>
    /// An <c>Include</c> that names a file rather than an assembly identity:
    /// contains a directory separator or ends in <c>.dll</c>. A bare name
    /// like <c>System.Data</c> is an assembly reference, not a path.
    /// </summary>
    private static bool LooksLikeReferencePath(string include) =>
        include.Contains('/') ||
        include.Contains('\\') ||
        include.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The package-cache roots a <see cref="PortableExecutableReference"/>
    /// must sit under to count as a materialized package reference (issue
    /// #577): the environment-derived set computed once — process
    /// environment and profile paths do not change mid-process — plus any
    /// roots the test seam adds.
    /// </summary>
    private static IReadOnlyList<string> NuGetPackageRoots()
    {
        var computed = NuGetPackageRootsCache.Value;
        var extra = ExtraNuGetPackageRoots.Value;
        if (extra is not { Count: > 0 })
        {
            return computed;
        }

        var merged = new List<string>(computed.Count + extra.Count);
        merged.AddRange(computed);
        merged.AddRange(extra);
        return merged;
    }

    /// <summary>
    /// The global package cache plus every fallback folder NuGet consults:
    /// <paramref name="nugetPackagesEnv"/> (<c>NUGET_PACKAGES</c>) replaces
    /// the profile default <c>~/.nuget/packages</c>, and
    /// <paramref name="nugetFallbackEnv"/> (<c>NUGET_FALLBACK_PACKAGES</c>)
    /// is a comma/semicolon list of extra roots — a fallback folder still
    /// counts as materialization. The dotnet SDK's own
    /// <c>NuGetFallbackFolder</c> is deliberately not derived: ref-pack
    /// assemblies under it are not package restore output, and counting it
    /// would let a framework-only compilation pass as restored. Entries are
    /// normalized to full paths and deduplicated; anything that cannot be
    /// resolved is dropped — an empty result means "cannot tell", and the
    /// caller stays conservative rather than degrade.
    /// </summary>
    internal static IReadOnlyList<string> ComputeNuGetPackageRoots(
        string? nugetPackagesEnv,
        string? nugetFallbackEnv,
        string? userProfilePath)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(nugetPackagesEnv))
        {
            AddRoot(nugetPackagesEnv.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(userProfilePath))
        {
            AddRoot(Path.Combine(userProfilePath, ".nuget", "packages"));
        }

        if (!string.IsNullOrWhiteSpace(nugetFallbackEnv))
        {
            foreach (var entry in nugetFallbackEnv.Split(
                         [';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddRoot(entry);
            }
        }

        return roots;

        void AddRoot(string candidate)
        {
            string full;
            try
            {
                full = Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }

            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length == 0 || roots.Contains(trimmed, PathComparer))
            {
                return;
            }

            roots.Add(trimmed);
        }
    }

    /// <summary>
    /// Whether any <see cref="PortableExecutableReference"/> in the
    /// compilation resolves under a NuGet package-cache root — the
    /// snapshot-level proof that a declared <c>&lt;PackageReference&gt;</c>
    /// materialized (issue #577). Framework ref-pack references land under
    /// the dotnet install tree, never a package-cache root, so a project
    /// holding only those still reports degraded. <see langword="internal"/>
    /// so tests can feed the root set directly.
    /// </summary>
    internal static bool HoldsPackageCacheReference(
        Compilation? compilation,
        IReadOnlyList<string> packageRoots)
    {
        if (compilation is null)
        {
            return false;
        }

        foreach (var reference in compilation.References)
        {
            if (reference is not PortableExecutableReference pe)
            {
                continue;
            }

            var path = pe.FilePath ?? pe.Display;
            if (path is not null && PathUnderAnyRoot(path, packageRoots))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="path"/> is at or below one of
    /// <paramref name="roots"/> — full-path normalized, separator-boundary
    /// prefix match under the OS path-comparison rule (a sibling directory
    /// sharing the root's name prefix does not count).
    /// </summary>
    private static bool PathUnderAnyRoot(string path, IReadOnlyList<string> roots)
    {
        if (!Path.IsPathRooted(path))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (var root in roots)
        {
            if (full.Equals(root, PathComparison) ||
                full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static readonly Lazy<IReadOnlyList<string>> NuGetPackageRootsCache = new(
        static () => ComputeNuGetPackageRoots(
            Environment.GetEnvironmentVariable("NUGET_PACKAGES"),
            Environment.GetEnvironmentVariable("NUGET_FALLBACK_PACKAGES"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

    /// <summary>
    /// Test seam (issue #577): extra package-cache roots appended to the
    /// environment-derived set, so a fixture can park a fake feed under its
    /// own temp dir without touching the real cache or the process
    /// environment. AsyncLocal so tests running in parallel never observe
    /// each other's roots.
    /// </summary>
    internal static readonly AsyncLocal<IReadOnlyList<string>?> ExtraNuGetPackageRoots = new();

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
    /// Matches a <c>&lt;PackageReference&gt;</c> item carrying an
    /// <c>Include</c> — a declared package dependency the searched-degraded
    /// check expects <c>obj/project.assets.json</c> to back (issue #572).
    /// </summary>
    private static readonly Regex PackageReferenceInclude = new(
        "<PackageReference\\b[^>]*\\bInclude\\s*=",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Matches a <c>&lt;Reference&gt;</c> item carrying an <c>Include</c> — a
    /// declared assembly reference for the searched-degraded check. An
    /// <c>&lt;Update&gt;</c>/<c>&lt;Remove&gt;</c>-only item does not
    /// declare a dependency, so <c>Include</c> is required.
    /// </summary>
    private static readonly Regex ReferenceInclude = new(
        "<Reference\\b[^>]*\\bInclude\\s*=",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Matches one <c>&lt;Reference&gt;</c> item with its <c>include</c>
    /// attribute value and — for the element form — its <c>body</c> so a
    /// declared <c>&lt;HintPath&gt;</c> can be read out of it (issue #577).
    /// </summary>
    private static readonly Regex ReferenceItem = new(
        "<Reference\\b[^>]*?\\bInclude\\s*=\\s*[\"'](?<include>[^\"']+)[\"'][^>]*?(?:/>|>(?<body>.*?)</Reference\\s*>)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>
    /// Matches a <c>&lt;HintPath&gt;</c> element inside a
    /// <c>&lt;Reference&gt;</c> body; the <c>hint</c> group carries the
    /// declared path text (issue #577).
    /// </summary>
    private static readonly Regex HintPathElement = new(
        "<HintPath\\b[^>]*>(?<hint>[^<]+)</HintPath\\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Matches an XML comment — commented-out items are stripped before the
    /// declared-items checks so a kept-for-later declaration never counts
    /// as a real one.
    /// </summary>
    private static readonly Regex XmlComment = new(
        "<!--.*?-->",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

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
