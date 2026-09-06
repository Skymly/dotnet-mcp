using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Server;

public sealed record LoadProgress(int CompletedUnits, int TotalUnits);

public sealed record DocumentDrift(
    string Path,
    string Kind,
    bool Repaired);

public sealed class LoadedSolution : IAsyncDisposable
{
    private readonly Workspace _workspace;
    private Dictionary<string, DocumentId> _docsByPath;
    private readonly Dictionary<string, DateTime> _projectFileMtimes = new(PathPolicy.Comparer);

    public LoadedSolution(
        Workspace workspace,
        Solution solution,
        IReadOnlyList<string> warnings)
    {
        _workspace = workspace;
        Solution = solution;
        Warnings = warnings;
        _docsByPath = BuildIndex(solution);

        var projectPaths = solution.Projects
            .Select(static p => p.FilePath)
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Cast<string>();
        RecordProjectFileSnapshots(projectPaths);
    }

    public Solution Solution { get; private set; }
    public IReadOnlyList<string> Warnings { get; }
    public Workspace Workspace => _workspace;

    public IReadOnlyCollection<string> TrackedDocumentPaths => _docsByPath.Keys;

    public bool TryGetDocumentId(string fullPath, out DocumentId documentId) =>
        _docsByPath.TryGetValue(Normalize(fullPath), out documentId!);

    public bool TryUpdateDocumentFromText(string fullPath, SourceText text)
    {
        if (!_docsByPath.TryGetValue(Normalize(fullPath), out var documentId))
        {
            return false;
        }

        var updated = Solution.WithDocumentText(documentId, text);
        if (!_workspace.TryApplyChanges(updated))
        {
            return false;
        }

        Solution = _workspace.CurrentSolution;
        _docsByPath = BuildIndex(Solution);
        return true;
    }

    /// <summary>
    /// Read-only drift scan. Performs disk I/O; do not hold the host gate across this call.
    /// </summary>
    public IReadOnlyList<DocumentDrift> DetectDrift(IReadOnlyList<string> extraProjectOrSolutionPaths, Func<string, bool>? allowRead = null)
    {
        var drifts = new List<DocumentDrift>();

        foreach (var (path, documentId) in _docsByPath.ToArray())
        {
            var document = Solution.GetDocument(documentId);
            if (document is null)
            {
                continue;
            }

            if (!File.Exists(path))
            {
                drifts.Add(new DocumentDrift(path, "MissingOnDisk", Repaired: false));
                continue;
            }

            if (allowRead is not null && !allowRead(path))
            {
                drifts.Add(new DocumentDrift(path, "OutsideTrustedRoots", Repaired: false));
                continue;
            }

            var workspaceText = document.GetTextAsync(CancellationToken.None).GetAwaiter().GetResult().ToString();
            var diskText = File.ReadAllText(path);
            if (!string.Equals(workspaceText, diskText, StringComparison.Ordinal))
            {
                drifts.Add(new DocumentDrift(path, "ContentMismatch", Repaired: false));
            }
        }

        foreach (var projectPath in extraProjectOrSolutionPaths
                     .Where(static p => !string.IsNullOrWhiteSpace(p))
                     .Select(Normalize)
                     .Distinct(PathPolicy.Comparer))
        {
            if (_docsByPath.ContainsKey(projectPath))
            {
                continue;
            }

            if (allowRead is not null && !allowRead(projectPath))
            {
                drifts.Add(new DocumentDrift(projectPath, "OutsideTrustedRoots", Repaired: false));
                continue;
            }

            if (!File.Exists(projectPath))
            {
                drifts.Add(new DocumentDrift(projectPath, "MissingOnDisk", Repaired: false));
                continue;
            }

            if (_projectFileMtimes.TryGetValue(projectPath, out var knownMtime))
            {
                var current = File.GetLastWriteTimeUtc(projectPath);
                if (current != knownMtime)
                {
                    drifts.Add(new DocumentDrift(projectPath, "ProjectFileChanged", Repaired: false));
                }
            }
        }

        return drifts;
    }

    /// <summary>
    /// Apply source repairs using pre-read disk text. Caller must serialize mutations (host gate).
    /// </summary>
    public IReadOnlyList<DocumentDrift> RepairSourceDrifts(
        IReadOnlyList<DocumentDrift> detected,
        IReadOnlyDictionary<string, string> diskTexts)
    {
        var result = new List<DocumentDrift>(detected.Count);
        foreach (var drift in detected)
        {
            if (drift.Kind == "ContentMismatch"
                && IsSourceFile(drift.Path)
                && diskTexts.TryGetValue(Normalize(drift.Path), out var diskText)
                && TryUpdateDocumentFromText(drift.Path, SourceText.From(diskText)))
            {
                result.Add(drift with { Kind = "ContentMismatchRepaired", Repaired = true });
                continue;
            }

            result.Add(drift);
        }

        return result;
    }

    public void RecordProjectFileSnapshots(IEnumerable<string> paths)
    {
        foreach (var path in paths
                     .Where(static p => !string.IsNullOrWhiteSpace(p))
                     .Select(Normalize)
                     .Distinct(PathPolicy.Comparer))
        {
            if (File.Exists(path))
            {
                _projectFileMtimes[path] = File.GetLastWriteTimeUtc(path);
            }
        }
    }

    public IReadOnlyCollection<string> TrackedProjectFilePaths => _projectFileMtimes.Keys;

    public ValueTask DisposeAsync()
    {
        if (_workspace is IDisposable disposable)
        {
            disposable.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    public static bool IsProjectOrSolutionFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".sln", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".slnf", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSourceFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".vb", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".fs", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".fsx", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWatchedFile(string path) =>
        IsSourceFile(path) || IsProjectOrSolutionFile(path);

    private static Dictionary<string, DocumentId> BuildIndex(Solution solution)
    {
        var map = new Dictionary<string, DocumentId>(PathPolicy.Comparer);
        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (string.IsNullOrWhiteSpace(document.FilePath))
                {
                    continue;
                }

                try
                {
                    map[Normalize(document.FilePath)] = document.Id;
                }
                catch (PathPolicyException)
                {
                    // Fail closed: unresolvable reparse points stay out of the index.
                }
            }
        }

        return map;
    }

    private static string Normalize(string path)
    {
        try
        {
            return PathPolicy.Normalize(path);
        }
        catch (PathPolicyException)
        {
            return Path.GetFullPath(path);
        }
    }
}

public interface ISolutionLoader
{
    Task<LoadedSolution> OpenAsync(
        string path,
        IProgress<LoadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
