using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
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
    // Concurrent: refresh PLANNING reads it outside the workspace gate while a committing
    // apply may write it inside the gate (issue #549).
    private readonly ConcurrentDictionary<string, AnalyzerFileStamp> _analyzerStamps = new(PathPolicy.Comparer);

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

    public bool TryGetDocumentId(string fullPath, out DocumentId documentId)
    {
        var normalized = TryNormalize(fullPath);
        if (normalized is null)
        {
            documentId = null!;
            return false;
        }

        return _docsByPath.TryGetValue(normalized, out documentId!);
    }

    public bool TryUpdateDocumentFromText(string fullPath, SourceText text)
    {
        if (!TryGetTrackedText(fullPath, out var documentId, out var current))
        {
            return false;
        }

        if (current.ContentEquals(text))
        {
            return false;
        }

        var updated = Solution.GetDocument(documentId) is not null
            ? Solution.WithDocumentText(documentId, text)
            : Solution.GetAdditionalDocument(documentId) is not null
                ? Solution.WithAdditionalDocumentText(documentId, text)
                : null;
        if (updated is null || !_workspace.TryApplyChanges(updated))
        {
            return false;
        }

        Solution = _workspace.CurrentSolution;
        _docsByPath = BuildIndex(Solution);
        return true;
    }

    internal bool HasMatchingDocumentText(string fullPath, SourceText text) =>
        TryGetTrackedText(fullPath, out _, out var current) && current.ContentEquals(text);

    private bool TryGetTrackedText(string fullPath, out DocumentId documentId, out SourceText text)
    {
        documentId = null!;
        text = null!;
        var normalized = TryNormalize(fullPath);
        if (normalized is null || !_docsByPath.TryGetValue(normalized, out documentId!))
        {
            return false;
        }

        var document = (TextDocument?)Solution.GetDocument(documentId)
            ?? Solution.GetAdditionalDocument(documentId);
        if (document is null)
        {
            return false;
        }

        if (!document.TryGetText(out var current))
        {
            current = document.GetTextAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        text = current;
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
            var document = (TextDocument?)Solution.GetDocument(documentId)
                ?? Solution.GetAdditionalDocument(documentId);
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

            if (!TryReadTrustedDiskText(path, allowRead, out var diskText))
            {
                // Exists lexically but the canonical target is unreadable or escaped.
                if (allowRead is not null)
                {
                    drifts.Add(new DocumentDrift(path, "OutsideTrustedRoots", Repaired: false));
                }

                continue;
            }

            var workspaceText = document.GetTextAsync(CancellationToken.None).GetAwaiter().GetResult().ToString();
            if (!string.Equals(workspaceText, diskText, StringComparison.Ordinal))
            {
                drifts.Add(new DocumentDrift(path, "ContentMismatch", Repaired: false));
            }
        }

        foreach (var projectPath in extraProjectOrSolutionPaths
                     .Where(static p => !string.IsNullOrWhiteSpace(p))
                     .Select(TryNormalize)
                     .OfType<string>()
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
            var normalized = TryNormalize(drift.Path);
            if (drift.Kind == "ContentMismatch"
                && IsSourceFile(drift.Path)
                && normalized is not null
                && diskTexts.TryGetValue(normalized, out var diskText)
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
                     .Select(TryNormalize)
                     .OfType<string>()
                     .Distinct(PathPolicy.Comparer))
        {
            if (File.Exists(path))
            {
                _projectFileMtimes[path] = File.GetLastWriteTimeUtc(path);
            }
        }
    }

    /// <summary>Append a load warning when the backing list is mutable; fixture loaders may pass immutable lists.</summary>
    internal void AddWarning(string message)
    {
        if (Warnings is List<string> warnings)
        {
            warnings.Add(message);
        }
    }

    /// <summary>
    /// Issue #549: swaps lazy file-backed analyzer references for
    /// <see cref="AnalyzerFileReference"/> instances that byte-load through
    /// <see cref="NonLockingAnalyzerLoader"/> — the DLL is never memory-mapped, so it is
    /// never locked for the session — and records each analyzer path's file stamp for
    /// <see cref="PlanAnalyzerReferenceRefresh"/>. Runs once after the trusted-graph gate,
    /// before the workspace is published. Returns the number of swapped references, or a
    /// negative number when the workspace refused the apply — the original references
    /// stay in place either way (today's behavior).
    /// </summary>
    internal int ConvertToNonLockingAnalyzerReferences(Func<string, bool> allowRead)
    {
        ArgumentNullException.ThrowIfNull(allowRead);
        var loader = new NonLockingAnalyzerLoader(allowRead);
        var swapped = 0;
        var updated = Solution;

        // Analyzer paths also serve as dependency probes, matching what MSBuild resolves.
        foreach (var path in EnumerateAnalyzerPaths(Solution))
        {
            loader.AddDependencyLocation(path);
        }

        foreach (var project in Solution.Projects)
        {
            var replacement = new List<AnalyzerReference>(project.AnalyzerReferences.Count);
            var touched = false;
            foreach (var reference in project.AnalyzerReferences)
            {
                var path = reference.FullPath;
                var normalized = path is null ? null : TryNormalize(path);
                if (normalized is not null)
                {
                    _analyzerStamps[normalized] = AnalyzerFileStamp.Capture(path!);
                }

                if (reference is AnalyzerFileReference
                    && normalized is not null
                    && File.Exists(path))
                {
                    replacement.Add(new AnalyzerFileReference(path!, loader));
                    touched = true;
                    swapped++;
                }
                else
                {
                    replacement.Add(reference);
                }
            }

            if (touched)
            {
                updated = updated.WithProjectAnalyzerReferences(project.Id, replacement);
            }
        }

        if (swapped > 0 && !TryApplySolutionChanges(updated))
        {
            return -swapped;
        }

        return swapped;
    }

    /// <summary>
    /// Issue #549: re-stat every analyzer path (disk I/O — call outside the workspace gate)
    /// and plan reference swaps where the file stamp moved since load or last refresh, or
    /// where an unresolved analyzer's DLL appeared meanwhile. Unchanged paths keep their
    /// reference instance — no swap, no churn.
    /// </summary>
    internal AnalyzerRefreshPlan? PlanAnalyzerReferenceRefresh(Func<string, bool> allowRead)
    {
        ArgumentNullException.ThrowIfNull(allowRead);
        var swaps = new List<KeyValuePair<ProjectId, IReadOnlyList<AnalyzerReference>>>();
        var newStamps = new Dictionary<string, AnalyzerFileStamp>(PathPolicy.Comparer);
        var loadersByPath = new Dictionary<string, NonLockingAnalyzerLoader>(PathPolicy.Comparer);

        foreach (var project in Solution.Projects)
        {
            List<AnalyzerReference>? replacement = null;
            var index = -1;
            foreach (var reference in project.AnalyzerReferences)
            {
                index++;
                var path = reference.FullPath;
                var normalized = path is null ? null : TryNormalize(path);
                if (normalized is null)
                {
                    continue;
                }

                _analyzerStamps.TryGetValue(normalized, out var known);
                var current = AnalyzerFileStamp.Capture(normalized);

                // A file-backed reference whose bytes changed gets a fresh reference (fresh
                // loader instance → fresh load context, and the new instance compares
                // unequal so the solution diff applies). A non-file reference such as
                // UnresolvedAnalyzerReference upgrades only on the missing→present
                // transition, preserving MSBuild's unresolved verdict while it was absent.
                var needsSwap = reference is AnalyzerFileReference
                    ? current.Exists && !current.Equals(known)
                    : current.Exists && !known.Exists;
                if (!needsSwap)
                {
                    continue;
                }

                if (!loadersByPath.TryGetValue(normalized, out var loader))
                {
                    loader = new NonLockingAnalyzerLoader(allowRead);
                    loadersByPath[normalized] = loader;
                }

                replacement ??= [.. project.AnalyzerReferences];
                replacement[index] = new AnalyzerFileReference(reference.FullPath!, loader);
                newStamps[normalized] = current;
            }

            if (replacement is not null)
            {
                swaps.Add(new KeyValuePair<ProjectId, IReadOnlyList<AnalyzerReference>>(project.Id, replacement));
            }
        }

        if (swaps.Count == 0)
        {
            return null;
        }

        foreach (var path in EnumerateAnalyzerPaths(Solution))
        {
            foreach (var loader in loadersByPath.Values)
            {
                loader.AddDependencyLocation(path);
            }
        }

        return new AnalyzerRefreshPlan(swaps, newStamps);
    }

    /// <summary>
    /// Applies a <see cref="PlanAnalyzerReferenceRefresh"/> result. Call under the workspace
    /// gate. A refused apply leaves references as they were (stale-but-stable, today's
    /// behavior) and skips the stamp update so the next epoch retries.
    /// </summary>
    internal void ApplyAnalyzerReferenceRefresh(AnalyzerRefreshPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var updated = Solution;
        foreach (var swap in plan.Swaps)
        {
            if (updated.GetProject(swap.Key) is not null)
            {
                updated = updated.WithProjectAnalyzerReferences(swap.Key, swap.Value);
            }
        }

        if (ReferenceEquals(updated, Solution) || !TryApplySolutionChanges(updated))
        {
            return;
        }

        foreach (var (path, stamp) in plan.NewStamps)
        {
            _analyzerStamps[path] = stamp;
        }
    }

    /// <summary>
    /// Routes a solution mutation through the underlying workspace so this snapshot and
    /// <see cref="Workspace.CurrentSolution"/> stay paired. Returns false when the
    /// workspace refuses the change.
    /// </summary>
    private bool TryApplySolutionChanges(Solution updated)
    {
        bool applied;
        try
        {
            applied = _workspace.TryApplyChanges(updated);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            applied = false;
        }

        Solution = _workspace.CurrentSolution;
        return applied;
    }

    private static IEnumerable<string> EnumerateAnalyzerPaths(Solution solution)
    {
        foreach (var project in solution.Projects)
        {
            foreach (var reference in project.AnalyzerReferences)
            {
                if (!string.IsNullOrWhiteSpace(reference.FullPath))
                {
                    yield return reference.FullPath!;
                }
            }
        }
    }

    public IReadOnlyCollection<string> TrackedProjectFilePaths => _projectFileMtimes.Keys;

    internal bool IsDisposed { get; private set; }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
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
               || ext.Equals(".fsi", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".fsx", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".axaml", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".xaml", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWatchedFile(string path) =>
        IsSourceFile(path) || IsProjectOrSolutionFile(path);

    /// <summary>
    /// Read a path only after canonicalize + trusted-root check. Follows the apply-time
    /// leaf-retarget rule so a symlink swap cannot pull outside content into the workspace.
    /// </summary>
    public static bool TryReadTrustedDiskText(string path, TrustedRoots trustedRoots, out string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(trustedRoots);
        return TryReadTrustedDiskText(path, trustedRoots.Contains, out text);
    }

    public static bool TryReadTrustedDiskText(string path, Func<string, bool>? allowRead, out string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        text = string.Empty;

        string canonical;
        try
        {
            canonical = PathPolicy.Normalize(path);
        }
        catch (PathPolicyException)
        {
            return false;
        }

        if (allowRead is not null && !allowRead(canonical))
        {
            return false;
        }

        try
        {
            if (!File.Exists(canonical))
            {
                return false;
            }

            text = File.ReadAllText(canonical);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Dictionary<string, DocumentId> BuildIndex(Solution solution)
    {
        var map = new Dictionary<string, DocumentId>(PathPolicy.Comparer);
        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents.Concat<TextDocument>(project.AdditionalDocuments))
            {
                if (string.IsNullOrWhiteSpace(document.FilePath))
                {
                    continue;
                }

                var normalized = TryNormalize(document.FilePath);
                if (normalized is null)
                {
                    // Fail closed: unresolvable reparse points stay out of the index.
                    continue;
                }

                map[normalized] = document.Id;
            }
        }

        return map;
    }

    private static string? TryNormalize(string path)
    {
        try
        {
            return PathPolicy.Normalize(path);
        }
        catch (Exception ex) when (ex is PathPolicyException or ArgumentException)
        {
            return null;
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
