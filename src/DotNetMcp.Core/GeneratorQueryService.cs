using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Core;

public sealed class GeneratorQueryService
{
    public const int DefaultGeneratedSourcesPageLimit = 50;
    public const int MaxGeneratedSourcesPageLimit = 100;

    private readonly ConcurrentDictionary<(string ProjectId, long Epoch), IReadOnlyList<GeneratorIdentity>> _listCache =
        new();

    private readonly ConcurrentDictionary<(string ProjectId, long Epoch), DriverRunSnapshot> _driverCache =
        new();

    public Task<(IReadOnlyList<GeneratorIdentity>? Success, SymbolQueryError? Error)> ListGeneratorsAsync(
        Solution solution,
        string projectId,
        long epoch,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryResolveCSharpProject(solution, projectId, out var project, out var error))
        {
            return Task.FromResult<(IReadOnlyList<GeneratorIdentity>?, SymbolQueryError?)>((null, error));
        }

        var cacheKey = (project!.Id.Id.ToString("D"), epoch);
        if (_listCache.TryGetValue(cacheKey, out var cached))
        {
            return Task.FromResult<(IReadOnlyList<GeneratorIdentity>?, SymbolQueryError?)>((cached, null));
        }

        var identities = EnumerateGenerators(project);
        _listCache[cacheKey] = identities;
        return Task.FromResult<(IReadOnlyList<GeneratorIdentity>?, SymbolQueryError?)>((identities, null));
    }

    public async Task<(PagedResult<GeneratedSourceItem>? Success, SymbolQueryError? Error)> ListGeneratedSourcesAsync(
        Solution solution,
        string projectId,
        long epoch,
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

        var offset = 0;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!GeneratedSourcesPageCursor.TryDecode(
                    cursor,
                    out var cursorEpoch,
                    out var cursorAssembly,
                    out var cursorType,
                    out offset,
                    out var cursorError))
            {
                return (null, new StaleCursorError(
                    cursorError ?? "Cursor is invalid.",
                    "Call project_list_generated_sources without a cursor to start a new page."));
            }

            if (cursorEpoch != epoch)
            {
                return (null, new StaleCursorError(
                    $"Cursor epoch {cursorEpoch} does not match workspace epoch {epoch}.",
                    "Call project_list_generated_sources without a cursor after the workspace refreshes."));
            }

            if (!string.Equals(cursorAssembly, assemblyName, StringComparison.Ordinal) ||
                !string.Equals(cursorType, typeFullName, StringComparison.Ordinal))
            {
                return (null, new StaleCursorError(
                    "Cursor generator identity does not match assemblyName/typeFullName.",
                    "Pass the same assemblyName and typeFullName used when the cursor was issued."));
            }
        }

        var (snapshot, snapError) = await GetDriverRunAsync(solution, projectId, epoch, cancellationToken)
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
        if (offset > run.Sources.Count)
        {
            return (null, new StaleCursorError(
                $"Cursor offset {offset} is beyond the generated-source list.",
                "Call project_list_generated_sources without a cursor to start a new page."));
        }

        var page = run.Sources.Skip(offset).Take(pageLimit).ToArray();
        var nextOffset = offset + page.Length;
        var truncated = nextOffset < run.Sources.Count;
        string? nextCursor = truncated
            ? GeneratedSourcesPageCursor.Encode(epoch, assemblyName, typeFullName, nextOffset)
            : null;

        var message = truncated
            ? $"Returning {page.Length} generated source(s); more remain — use nextCursor."
            : $"Returning {page.Length} generated source(s).";

        return (new PagedResult<GeneratedSourceItem>(page, truncated, nextCursor, message), null);
    }

    public async Task<(DriverRunSnapshot? Success, SymbolQueryError? Error)> GetDriverRunAsync(
        Solution solution,
        string projectId,
        long epoch,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryResolveCSharpProject(solution, projectId, out var project, out var error))
        {
            return (null, error);
        }

        var cacheKey = (project!.Id.Id.ToString("D"), epoch);
        if (_driverCache.TryGetValue(cacheKey, out var cached))
        {
            return (cached, null);
        }

        try
        {
            var snapshot = await GeneratorDriverRunner.RunOnProjectAsync(project, cancellationToken)
                .ConfigureAwait(false);
            _driverCache[cacheKey] = snapshot;
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
        Solution solution,
        string projectId,
        long epoch,
        SyntaxTree tree,
        CancellationToken cancellationToken = default)
    {
        var (snapshot, error) = await GetDriverRunAsync(solution, projectId, epoch, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        return (GeneratorDriverRunner.MatchTree(snapshot!, tree), null);
    }

    private static bool TryResolveCSharpProject(
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
            .Where(p => p.Language == LanguageNames.CSharp)
            .FirstOrDefault(p =>
                string.Equals(p.Id.Id.ToString("D"), projectId, StringComparison.OrdinalIgnoreCase));

        if (project is null)
        {
            error = new ProjectNotFoundError(
                $"No C# project with projectId '{projectId}' is in the ready workspace.",
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
            foreach (var generator in reference.GetGenerators(LanguageNames.CSharp))
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
