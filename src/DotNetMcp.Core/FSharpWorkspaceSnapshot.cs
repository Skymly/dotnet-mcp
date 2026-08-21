namespace DotNetMcp.Core;

/// <summary>
/// F# project/source snapshot frozen with a workspace Epoch.
/// Sits beside the Roslyn compilation graph; FCS reads this, not Roslyn types.
/// </summary>
public sealed class FSharpWorkspaceSnapshot
{
    public FSharpWorkspaceSnapshot(long epoch, IReadOnlyList<FSharpProjectSnapshot> projects)
    {
        Epoch = epoch;
        Projects = projects;
    }

    public long Epoch { get; }

    public IReadOnlyList<FSharpProjectSnapshot> Projects { get; }

    public FSharpProjectSnapshot? FindProject(string projectId) =>
        Projects.FirstOrDefault(p =>
            string.Equals(p.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));
}

public sealed class FSharpProjectSnapshot
{
    public FSharpProjectSnapshot(
        string projectId,
        string name,
        string? filePath,
        IReadOnlyList<FSharpDocumentSnapshot> documents)
    {
        ProjectId = projectId;
        Name = name;
        FilePath = filePath;
        Documents = documents;
    }

    public string ProjectId { get; }

    public string Name { get; }

    public string? FilePath { get; }

    public IReadOnlyList<FSharpDocumentSnapshot> Documents { get; }
}

public sealed class FSharpDocumentSnapshot
{
    public FSharpDocumentSnapshot(string path, string text)
    {
        Path = path;
        Text = text;
    }

    public string Path { get; }

    public string Text { get; }
}
