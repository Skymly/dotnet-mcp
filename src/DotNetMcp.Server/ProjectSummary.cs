using System.Text.RegularExpressions;
using DotNetMcp.Core;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Server;

public static partial class ProjectSummary
{
    [GeneratedRegex(@"\(([^)]+)\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TfmSuffixRegex();

    public static IReadOnlyList<ProjectSummaryDto> FromSolution(Solution solution)
    {
        return solution.Projects
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id.Id)
            .Select(p => new ProjectSummaryDto
            {
                ProjectId = p.Id.Id.ToString("D"),
                Name = p.Name,
                Language = RoslynLanguageAdapter.LanguageToken(p.Language),
                TargetFramework = ExtractTfm(p),
                FilePath = p.FilePath
            })
            .ToArray();
    }

    public static string? ExtractTfm(Project project)
    {
        var match = TfmSuffixRegex().Match(project.Name);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        // Fallback: some hosts omit the Name suffix when a single TFM is forced.
        if (project.ParseOptions is { } parseOptions)
        {
            foreach (var symbol in parseOptions.PreprocessorSymbolNames)
            {
                if (symbol.StartsWith("NET", StringComparison.OrdinalIgnoreCase) &&
                    symbol.Contains('_', StringComparison.Ordinal))
                {
                    // e.g. NET8_0 → net8.0
                    var body = symbol[3..].Replace('_', '.');
                    if (body.Length > 0)
                    {
                        return "net" + body.ToLowerInvariant();
                    }
                }
            }
        }

        return null;
    }
}
