using DotNetMcp.Core;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Server;

/// <summary>
/// Request-scoped workspace snapshot implementing ADR-0002 compilation / generator APIs.
/// </summary>
public sealed class WorkspaceSession : IWorkspaceSession, IWorkspaceSessionCaches
{
    public const int DefaultCompilationLruCapacity = 50;

    private readonly CompilationLru _compilationLru;
    private readonly GeneratorRunCache _generatorRunCache;
    private readonly FindHitCache _findHits;
    private bool _disposed;

    public WorkspaceSession(
        LoadedSolution loaded,
        long epoch,
        int compilationLruCapacity = DefaultCompilationLruCapacity,
        GeneratorRunCache? generatorRunCache = null,
        CompilationLru? compilationLru = null,
        FindHitCache? findHitCache = null,
        FSharpWorkspaceSnapshot? fsharpSnapshot = null)
    {
        // Prefer the host-frozen F# snapshot (captured when Epoch advances). Fall back only for tests
        // that construct sessions without a host, still without re-walking disk when a snapshot is provided.
        Solution = loaded.Solution;
        Epoch = epoch;
        FSharpSnapshot = fsharpSnapshot ?? CaptureFSharpSnapshot(loaded.Solution, epoch, trustedRoots: null);
        _compilationLru = compilationLru ?? new CompilationLru(compilationLruCapacity);
        _generatorRunCache = generatorRunCache ?? new GeneratorRunCache();
        _findHits = findHitCache ?? new FindHitCache();
    }

    public long Epoch { get; }

    public Solution Solution { get; }

    public FSharpWorkspaceSnapshot FSharpSnapshot { get; }

    /// <summary>Test/observability hook for the compilation LRU (host-shared when injected).</summary>
    public CompilationLru CompilationCache => _compilationLru;

    public FindHitCache FindHits => _findHits;

    public IReadOnlyList<ProjectSummaryDto> ListProjects() =>
        ProjectSummary.FromSolution(Solution);

    public async Task<Compilation> GetCompilationAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var project = RequireProject(projectId);
        return await _compilationLru
            .GetOrAddAsync(project, CompileAsync, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Compilation> GetCompilationWithoutGeneratedTreesAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var project = RequireProject(projectId);
        var full = await GetCompilationAsync(projectId, cancellationToken).ConfigureAwait(false);
        return await GeneratorDriverRunner
            .StripGeneratedTreesFromProjectAsync(project, full, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DriverRunSnapshot> GetGeneratorRunResultAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var project = RequireProject(projectId);
        var key = project.Id.Id.ToString("D");

        if (_generatorRunCache.TryGet(key, Epoch, out var cached))
        {
            return cached;
        }

        var baseCompilation = await GetCompilationWithoutGeneratedTreesAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        var snapshot = GeneratorDriverRunner.RunDriver(project, baseCompilation, cancellationToken);
        _generatorRunCache.Set(key, Epoch, snapshot);
        return snapshot;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // LoadedSolution lifetime is owned by WorkspaceHost, not per-request Dispose.
    }

    private Project RequireProject(ProjectId projectId)
    {
        var project = Solution.GetProject(projectId)
            ?? throw new InvalidOperationException($"Project '{projectId.Id}' is not in the session solution.");
        return project;
    }

    private static async Task<Compilation> CompileAsync(Project project, CancellationToken cancellationToken)
    {
        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Compilation was null for project '{project.Name}'.");
        return compilation;
    }


    /// <summary>
    /// Capture F# sources once per Epoch. Skips symlink directories and paths outside trusted roots.
    /// </summary>
    public static FSharpWorkspaceSnapshot CaptureFSharpSnapshot(
        Solution solution,
        long epoch,
        TrustedRoots? trustedRoots)
    {
        var projects = new List<FSharpProjectSnapshot>();
        foreach (var project in solution.Projects)
        {
            if (!IsFSharpProject(project))
            {
                continue;
            }

            var documents = ReadFSharpDocuments(project, trustedRoots);
            projects.Add(new FSharpProjectSnapshot(
                project.Id.Id.ToString("D"),
                project.Name,
                project.FilePath,
                documents));
        }

        return new FSharpWorkspaceSnapshot(epoch, projects);
    }

    internal static bool IsFSharpProject(Project project) =>
        project.Language == LanguageNames.FSharp ||
        (project.FilePath?.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ?? false);

    private static IReadOnlyList<FSharpDocumentSnapshot> ReadFSharpDocuments(
        Project project,
        TrustedRoots? trustedRoots)
    {
        var documents = new List<FSharpDocumentSnapshot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path, string text, bool requireTrusted)
        {
            string full;
            try
            {
                full = PathPolicy.Normalize(path);
            }
            catch (PathPolicyException)
            {
                return;
            }

            // Disk enumeration must stay inside trusted roots. Roslyn documents already
            // admitted to the loaded graph are frozen from in-memory text (graph gate
            // rejects on-disk escapes at open); synthetic fixture paths are allowed.
            if (requireTrusted && trustedRoots is not null && !trustedRoots.ContainsNormalized(full))
            {
                return;
            }

            if (seen.Add(full))
            {
                documents.Add(new FSharpDocumentSnapshot(full, text));
            }
        }

        foreach (var document in project.Documents)
        {
            if (document.FilePath is null ||
                !document.FilePath.EndsWith(".fs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!document.TryGetText(out var sourceText))
            {
                sourceText = document.GetTextAsync(CancellationToken.None).GetAwaiter().GetResult();
            }

            Add(document.FilePath, sourceText.ToString(), requireTrusted: false);
        }

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(project.FilePath))
        {
            string projectFull;
            try
            {
                projectFull = PathPolicy.Normalize(project.FilePath);
            }
            catch (PathPolicyException)
            {
                projectFull = Path.GetFullPath(project.FilePath);
            }

            var dir = Path.GetDirectoryName(projectFull);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                roots.Add(dir);
            }
        }

        foreach (var existing in documents.ToArray())
        {
            var dir = Path.GetDirectoryName(existing.Path);
            while (!string.IsNullOrWhiteSpace(dir))
            {
                if (Path.GetFileName(dir) is "obj" or "bin")
                {
                    var parent = Path.GetDirectoryName(dir);
                    if (!string.IsNullOrWhiteSpace(parent))
                    {
                        roots.Add(parent);
                    }

                    break;
                }

                dir = Path.GetDirectoryName(dir);
            }
        }

        foreach (var root in roots)
        {
            if (!Directory.Exists(root) || IsSymlinkDirectory(root))
            {
                continue;
            }

            EnumerateFsFilesSkippingSymlinkDirs(
                root,
                (path, text) => Add(path, text, requireTrusted: true));
        }

        return documents;
    }

    private static void EnumerateFsFilesSkippingSymlinkDirs(string root, Action<string, string> add)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            if (IsSymlinkDirectory(dir))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.fs");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var path in files)
            {
                var relative = Path.GetRelativePath(root, path);
                if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                                     part.Equals("obj", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                try
                {
                    add(path, File.ReadAllText(path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            IEnumerable<string> subdirs;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsSymlinkDirectory(sub))
                {
                    continue;
                }

                stack.Push(sub);
            }
        }
    }

    private static bool IsSymlinkDirectory(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0 && Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

