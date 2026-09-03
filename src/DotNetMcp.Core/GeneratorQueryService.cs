using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Core;

public sealed class GeneratorQueryService
{
    public const int DefaultGeneratedSourcesPageLimit = 50;
    public const int MaxGeneratedSourcesPageLimit = 100;

    private readonly ConcurrentDictionary<(string ProjectId, long Epoch), IReadOnlyList<GeneratorIdentity>> _listCache =
        new();

    public Task<(IReadOnlyList<GeneratorIdentity>? Success, SymbolQueryError? Error)> ListGeneratorsAsync(
        IWorkspaceSession session,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryResolveSupportedProject(session.Solution, projectId, out var project, out var error))
        {
            return Task.FromResult<(IReadOnlyList<GeneratorIdentity>?, SymbolQueryError?)>((null, error));
        }

        var cacheKey = (project!.Id.Id.ToString("D"), session.Epoch);
        if (_listCache.TryGetValue(cacheKey, out var cached))
        {
            return Task.FromResult<(IReadOnlyList<GeneratorIdentity>?, SymbolQueryError?)>((cached, null));
        }

        var identities = EnumerateGenerators(project);
        _listCache[cacheKey] = identities;
        return Task.FromResult<(IReadOnlyList<GeneratorIdentity>?, SymbolQueryError?)>((identities, null));
    }

    public async Task<(PagedResult<GeneratedSourceItem>? Success, SymbolQueryError? Error)> ListGeneratedSourcesAsync(
        IWorkspaceSession session,
        string projectId,
        string assemblyName,
        string typeFullName,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(assemblyName) || string.IsNullOrWhiteSpace(typeFullName))
        {
            return (null, new GeneratorNotFoundError(
                "assemblyName and typeFullName are required to list generated sources.",
                "Call project_list_generators, then pass the generator AssemblyName and TypeFullName."));
        }

        assemblyName = assemblyName.Trim();
        typeFullName = typeFullName.Trim();
        var epoch = session.Epoch;

        var (snapshot, snapError) = await GetDriverRunAsync(session, projectId, cancellationToken)
            .ConfigureAwait(false);
        if (snapError is not null)
        {
            return (null, snapError);
        }

        var run = snapshot!.ByGenerator.FirstOrDefault(g =>
            string.Equals(g.Identity.AssemblyName, assemblyName, StringComparison.Ordinal) &&
            string.Equals(g.Identity.TypeFullName, typeFullName, StringComparison.Ordinal));

        if (run is null)
        {
            return (null, new GeneratorNotFoundError(
                $"No source generator '{assemblyName}::{typeFullName}' is registered on project '{projectId}'.",
                "Call project_list_generators for valid generator identities, then retry."));
        }

        var pageLimit = ClampLimit(limit);
        return SoftBudgetPage.PageGenerated(
            run.Sources,
            epoch,
            assemblyName,
            typeFullName,
            cursor,
            pageLimit,
            "project_list_generated_sources",
            "Generator produced no sources.",
            "Generated sources page complete.",
            "the generated-source list");
    }

    public async Task<(GeneratorDiagnosticsPage? Success, SymbolQueryError? Error)> ListGeneratorDiagnosticsAsync(
        IWorkspaceSession session,
        string projectId,
        string assemblyName,
        string typeFullName,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(assemblyName) || string.IsNullOrWhiteSpace(typeFullName))
        {
            return (null, new GeneratorNotFoundError(
                "assemblyName and typeFullName are required to list generator diagnostics.",
                "Call project_list_generators, then pass the generator AssemblyName and TypeFullName."));
        }

        assemblyName = assemblyName.Trim();
        typeFullName = typeFullName.Trim();
        var epoch = session.Epoch;

        var (snapshot, snapError) = await GetDriverRunAsync(session, projectId, cancellationToken)
            .ConfigureAwait(false);
        if (snapError is not null)
        {
            return (null, snapError);
        }

        var run = snapshot!.ByGenerator.FirstOrDefault(g =>
            string.Equals(g.Identity.AssemblyName, assemblyName, StringComparison.Ordinal) &&
            string.Equals(g.Identity.TypeFullName, typeFullName, StringComparison.Ordinal));

        if (run is null)
        {
            return (null, new GeneratorNotFoundError(
                $"No source generator '{assemblyName}::{typeFullName}' is registered on project '{projectId}'.",
                "Call project_list_generators for valid generator identities, then retry."));
        }

        var pageLimit = ClampLimit(limit);
        var (page, pageError) = SoftBudgetPage.PageGenerated(
            run.Diagnostics,
            epoch,
            assemblyName,
            typeFullName,
            cursor,
            pageLimit,
            "project_list_generator_diagnostics",
            "Generator produced no diagnostics.",
            "Generator diagnostics page complete.",
            "the generator-diagnostics list");
        if (pageError is not null)
        {
            return (null, pageError);
        }

        return (new GeneratorDiagnosticsPage(run.Identity, page!), null);
    }

    public async Task<(DriverRunSnapshot? Success, SymbolQueryError? Error)> GetDriverRunAsync(
        IWorkspaceSession session,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryResolveSupportedProject(session.Solution, projectId, out var project, out var error))
        {
            return (null, error);
        }

        try
        {
            var snapshot = await session
                .GetGeneratorRunResultAsync(project!.Id, cancellationToken)
                .ConfigureAwait(false);
            return (snapshot, null);
        }
        catch (InvalidOperationException ex)
        {
            return (null, new CompilationUnavailableError(
                ex.Message,
                "Ensure the project compiles, then retry after workspace_status reports ready."));
        }
    }

    public async Task<(GeneratorIdentity? Identity, SymbolQueryError? Error)> MatchSyntaxTreeAsync(
        IWorkspaceSession session,
        string projectId,
        SyntaxTree tree,
        CancellationToken cancellationToken = default)
    {
        var (snapshot, error) = await GetDriverRunAsync(session, projectId, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        var match = GeneratorDriverRunner.MatchTree(snapshot!, tree);
        if (match.Ambiguous)
        {
            return (null, new GeneratorAttributionAmbiguousError(
                "Generated source content matches more than one generator; Origin cannot be bound uniquely.",
                "Call project_list_generated_sources for each candidate generator; do not treat this symbol's Origin as a single generator identity."));
        }

        return (match.Identity, null);
    }

    private static bool TryResolveSupportedProject(
        Solution solution,
        string projectId,
        out Project? project,
        out SymbolQueryError? error)
    {
        project = null;
        error = null;

        if (string.IsNullOrWhiteSpace(projectId))
        {
            error = new ProjectNotFoundError(
                "projectId is required.",
                "Call workspace_list_projects for valid projectId values, then retry.");
            return false;
        }

        project = solution.Projects
            .Where(p => RoslynLanguageAdapter.IsSupportedRoslynLanguage(p.Language))
            .FirstOrDefault(p =>
                string.Equals(p.Id.Id.ToString("D"), projectId, StringComparison.OrdinalIgnoreCase));

        if (project is null)
        {
            error = new ProjectNotFoundError(
                $"No project with projectId '{projectId}' is in the ready workspace.",
                "Call workspace_list_projects for valid projectId values, then retry.");
            return false;
        }

        return true;
    }

    private static IReadOnlyList<GeneratorIdentity> EnumerateGenerators(Project project)
    {
        var seen = new HashSet<(string Assembly, string Type)>();
        var list = new List<GeneratorIdentity>();

        foreach (var reference in project.AnalyzerReferences)
        {
            foreach (var generator in reference.GetGenerators(project.Language))
            {
                var type = generator.GetGeneratorType();
                var assemblyName = type.Assembly.GetName().Name ?? string.Empty;
                var typeFullName = type.FullName ?? type.Name;
                if (!seen.Add((assemblyName, typeFullName)))
                {
                    continue;
                }

                var version = type.Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
                list.Add(new GeneratorIdentity(assemblyName, typeFullName, version));
            }
        }

        list.Sort(static (a, b) =>
        {
            var assembly = string.CompareOrdinal(a.AssemblyName, b.AssemblyName);
            return assembly != 0 ? assembly : string.CompareOrdinal(a.TypeFullName, b.TypeFullName);
        });

        return list;
    }

    private static int ClampLimit(int? limit)
    {
        if (limit is null or <= 0)
        {
            return DefaultGeneratedSourcesPageLimit;
        }

        return Math.Min(limit.Value, MaxGeneratedSourcesPageLimit);
    }
}
