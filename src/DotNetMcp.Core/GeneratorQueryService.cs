using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Core;

public sealed class GeneratorQueryService
{
    private readonly ConcurrentDictionary<(string ProjectId, long Epoch), IReadOnlyList<GeneratorIdentity>> _cache =
        new();

    public Task<(IReadOnlyList<GeneratorIdentity>? Success, SymbolQueryError? Error)> ListGeneratorsAsync(
        Solution solution,
        string projectId,
        long epoch,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(projectId))
        {
            return Task.FromResult<(IReadOnlyList<GeneratorIdentity>?, SymbolQueryError?)>((
                null,
                new ProjectNotFoundError(
                    "projectId is required.",
                    "Call workspace_list_projects for valid projectId values, then retry project_list_generators.")));
        }

        var project = solution.Projects
            .Where(p => p.Language == LanguageNames.CSharp)
            .FirstOrDefault(p =>
                string.Equals(p.Id.Id.ToString("D"), projectId, StringComparison.OrdinalIgnoreCase));

        if (project is null)
        {
            return Task.FromResult<(IReadOnlyList<GeneratorIdentity>?, SymbolQueryError?)>((
                null,
                new ProjectNotFoundError(
                    $"No C# project with projectId '{projectId}' is in the ready workspace.",
                    "Call workspace_list_projects for valid projectId values, then retry project_list_generators.")));
        }

        var cacheKey = (project.Id.Id.ToString("D"), epoch);
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return Task.FromResult<(IReadOnlyList<GeneratorIdentity>?, SymbolQueryError?)>((cached, null));
        }

        var identities = EnumerateGenerators(project);
        _cache[cacheKey] = identities;
        return Task.FromResult<(IReadOnlyList<GeneratorIdentity>?, SymbolQueryError?)>((identities, null));
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
}
