using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotNetMcp.Server;

/// <summary>
/// Self-parses a Visual Studio / .NET solution filter (.slnf) JSON file.
/// MSBuildWorkspace has no public API for .slnf (roslyn#73105). Ported from Spike S2.
/// </summary>
public static class SlnfParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static SlnfDocument ParseFile(string slnfPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slnfPath);
        if (!File.Exists(slnfPath))
        {
            throw new FileNotFoundException("Solution filter not found.", slnfPath);
        }

        var text = File.ReadAllText(slnfPath);
        var doc = JsonSerializer.Deserialize<SlnfDocument>(text, JsonOptions)
            ?? throw new InvalidDataException($"Failed to deserialize .slnf: {slnfPath}");

        if (doc.Solution is null || string.IsNullOrWhiteSpace(doc.Solution.Path))
        {
            throw new InvalidDataException($".slnf missing solution.path: {slnfPath}");
        }

        doc.Solution.Projects ??= [];
        return doc;
    }

    /// <summary>
    /// Resolves absolute project paths listed in the filter, relative to the solution directory.
    /// </summary>
    public static IReadOnlyList<string> ResolveProjectPaths(string slnfPath, SlnfDocument? document = null)
    {
        document ??= ParseFile(slnfPath);
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(slnfPath))
            ?? throw new InvalidOperationException("Cannot resolve .slnf directory.");

        var solutionRelative = document.Solution!.Path;
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(baseDir, solutionRelative)))
            ?? baseDir;

        var results = new List<string>(document.Solution.Projects!.Count);
        foreach (var relative in document.Solution.Projects)
        {
            if (string.IsNullOrWhiteSpace(relative))
            {
                continue;
            }

            results.Add(Path.GetFullPath(Path.Combine(solutionDir, relative)));
        }

        return results;
    }
}

public sealed class SlnfDocument
{
    [JsonPropertyName("solution")]
    public SlnfSolution? Solution { get; set; }
}

public sealed class SlnfSolution
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("projects")]
    public List<string>? Projects { get; set; }
}
