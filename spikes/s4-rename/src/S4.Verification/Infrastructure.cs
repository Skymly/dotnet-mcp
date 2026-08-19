using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DotNetMcp.Core;
using DotNetMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

namespace S4.Verification;

internal static class FixturePaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string SpikeRoot { get; } = Path.Combine(RepoRoot, "spikes", "s4-rename");

    public static string RenameAppDir { get; } = Path.Combine(SpikeRoot, "fixtures", "RenameApp");

    public static string DataDir { get; } = Path.Combine(SpikeRoot, "data");

    private static string FindRepoRoot([CallerFilePath] string? thisFile = null)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "docs", "adr")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root from test assembly path.");
    }
}

internal sealed record TextSlice(string Path, string OldText, string NewText);

internal sealed record RenamePreview(
    string PreviewId,
    long Epoch,
    DateTimeOffset ExpiresAt,
    string OldHandle,
    string NewName,
    IReadOnlyList<TextSlice> Documents);

internal sealed class ManualWorkspaceFileWatcher : IWorkspaceFileWatcher
{
    private Action<IReadOnlyList<string>>? _onPathsChanged;
    private bool _disposed;

    public bool IsStarted { get; private set; }

    public void Start(IReadOnlyList<string> roots, Action<IReadOnlyList<string>> onPathsChanged)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _onPathsChanged = onPathsChanged;
        IsStarted = true;
    }

    public void Stop()
    {
        IsStarted = false;
        _onPathsChanged = null;
    }

    public void Raise(params string[] paths)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_onPathsChanged is null || paths.Length == 0)
        {
            return;
        }

        _onPathsChanged(paths);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}

internal sealed class FixtureSolutionLoader : ISolutionLoader
{
    private readonly Func<LoadedSolution> _factory;

    public FixtureSolutionLoader(Func<LoadedSolution> factory) => _factory = factory;

    public Task<LoadedSolution> OpenAsync(
        string path,
        IProgress<LoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new LoadProgress(0, 1));
        cancellationToken.ThrowIfCancellationRequested();
        var loaded = _factory();
        progress?.Report(new LoadProgress(1, 1));
        return Task.FromResult(loaded);
    }
}

internal static class RenameWorkspace
{
    public static readonly SymbolRenameOptions DefaultOptions = new(
        RenameOverloads: false,
        RenameInStrings: false,
        RenameInComments: false,
        RenameFile: false);

    public static string CopyRenameApp(string tempDir)
    {
        Directory.CreateDirectory(tempDir);
        foreach (var file in Directory.GetFiles(FixturePaths.RenameAppDir))
        {
            File.Copy(file, Path.Combine(tempDir, Path.GetFileName(file)), overwrite: true);
        }

        return tempDir;
    }

    public static LoadedSolution LoadHandwritten(string projectDir, bool attachGenerator = false)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var projectPath = Path.Combine(projectDir, "RenameApp.csproj");
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "RenameApp",
            "RenameApp",
            LanguageNames.CSharp,
            filePath: projectPath));

        foreach (var path in Directory.GetFiles(projectDir, "*.cs"))
        {
            var docId = DocumentId.CreateNewId(projectId);
            solution = solution.AddDocument(
                docId,
                Path.GetFileName(path),
                SourceText.From(File.ReadAllText(path), Encoding.UTF8),
                filePath: path);
        }

        solution = solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        foreach (var metadata in TrustedPlatformReferences())
        {
            solution = solution.AddMetadataReference(projectId, metadata);
        }

        if (attachGenerator)
        {
            var generatorPath = typeof(CustomGenerator.MarkerGenerator).Assembly.Location;
            solution = solution.AddAnalyzerReference(
                projectId,
                new AnalyzerFileReference(generatorPath, TestAnalyzerAssemblyLoader.Instance));
        }

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace rename fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    public static LoadedSolution LoadGeneratedHost(string projectDir)
    {
        Directory.CreateDirectory(projectDir);
        var projectPath = Path.Combine(projectDir, "GeneratorHost.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var hostPath = Path.Combine(projectDir, "Host.cs");
        File.WriteAllText(
            hostPath,
            """
            namespace GeneratorHost;

            public static class Host
            {
                public static string Name => "host";
            }

            public partial class PartialThing
            {
                public string Format() => "hw";
            }
            """);

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "GeneratorHost",
            "GeneratorHost",
            LanguageNames.CSharp,
            filePath: projectPath));

        var docId = DocumentId.CreateNewId(projectId);
        solution = solution.AddDocument(
            docId,
            "Host.cs",
            SourceText.From(File.ReadAllText(hostPath), Encoding.UTF8),
            filePath: hostPath);
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        foreach (var metadata in TrustedPlatformReferences())
        {
            solution = solution.AddMetadataReference(projectId, metadata);
        }

        var generatorPath = typeof(CustomGenerator.MarkerGenerator).Assembly.Location;
        solution = solution.AddAnalyzerReference(
            projectId,
            new AnalyzerFileReference(generatorPath, TestAnalyzerAssemblyLoader.Instance));

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace generator fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    public static async Task<ISymbol> RequireSymbolAsync(
        WorkspaceSession session,
        string metadataName,
        CancellationToken cancellationToken = default)
    {
        var project = session.Solution.Projects.Single();
        var compilation = await session.GetCompilationAsync(project.Id, cancellationToken).ConfigureAwait(false);
        var type = compilation.GetTypeByMetadataName(metadataName)
            ?? throw new InvalidOperationException($"Type '{metadataName}' was not found.");
        return type;
    }

    public static async Task<ISymbol> RequireMethodAsync(
        WorkspaceSession session,
        string typeMetadataName,
        string methodName,
        CancellationToken cancellationToken = default)
    {
        var type = (INamedTypeSymbol)await RequireSymbolAsync(session, typeMetadataName, cancellationToken)
            .ConfigureAwait(false);
        return type.GetMembers(methodName).OfType<IMethodSymbol>().First(m => m.MethodKind == MethodKind.Ordinary);
    }

    public static async Task<(Solution NewSolution, IReadOnlyList<TextSlice> Slices, TimeSpan Elapsed)> PreviewRenameAsync(
        Solution solution,
        ISymbol symbol,
        string newName,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var renamed = await Renamer.RenameSymbolAsync(
            solution,
            symbol,
            DefaultOptions,
            newName,
            cancellationToken).ConfigureAwait(false);
        sw.Stop();

        var slices = new List<TextSlice>();
        var changes = renamed.GetChanges(solution);
        foreach (var projectChange in changes.GetProjectChanges())
        {
            foreach (var docId in projectChange.GetChangedDocuments())
            {
                var oldDoc = solution.GetDocument(docId);
                var newDoc = renamed.GetDocument(docId);
                if (oldDoc is null || newDoc is null)
                {
                    continue;
                }

                var oldText = (await oldDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                var newText = (await newDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                if (oldText == newText)
                {
                    continue;
                }

                slices.Add(new TextSlice(oldDoc.FilePath ?? oldDoc.Name, oldText, newText));
            }
        }

        return (renamed, slices, sw.Elapsed);
    }

    public static string SnapshotDisk(string dir) =>
        string.Join(
            "\n---\n",
            Directory.GetFiles(dir, "*.cs").OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => $"{Path.GetFileName(p)}\n{File.ReadAllText(p)}"));

    public static async Task WaitReadyAsync(WorkspaceHost host)
    {
        for (var i = 0; i < 80; i++)
        {
            var status = host.GetStatus();
            if (status.Phase == "ready")
            {
                return;
            }

            if (status.Phase is "failed" or "cancelled")
            {
                throw new InvalidOperationException($"Workspace load {status.Phase}: {status.Error}");
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new TimeoutException($"Workspace did not become ready: {host.GetStatus().Phase}");
    }

    public static IReadOnlyList<MetadataReference> TrustedPlatformReferences()
    {
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (string.IsNullOrWhiteSpace(tpa))
        {
            return
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
            ];
        }

        return tpa.Split(Path.PathSeparator)
            .Where(static p => p.EndsWith("System.Runtime.dll", StringComparison.OrdinalIgnoreCase)
                               || p.EndsWith("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase)
                               || p.EndsWith("System.Console.dll", StringComparison.OrdinalIgnoreCase)
                               || p.EndsWith("netstandard.dll", StringComparison.OrdinalIgnoreCase))
            .Select(static p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();
    }

    private sealed class TestAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
    {
        public static TestAnalyzerAssemblyLoader Instance { get; } = new();

        public void AddDependencyLocation(string fullPath)
        {
        }

        public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
    }
}

/// <summary>
/// Spike-local apply host: previewId + Epoch + TTL, write under suppression, backfill, bump epoch once.
/// </summary>
internal sealed class RenameApplyHost
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, RenamePreview> _previews = new(StringComparer.Ordinal);
    private LoadedSolution _loaded;
    private long _epoch = 1;

    public RenameApplyHost(LoadedSolution loaded) => _loaded = loaded;

    public WriteSuppression WriteSuppression { get; } = new();

    public long Epoch => _epoch;

    public WorkspaceSession Session() => new(_loaded, _epoch);

    public async Task<RenamePreview> PreviewAsync(
        ISymbol symbol,
        string newName,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        var session = Session();
        var project = session.Solution.Projects.Single();
        var oldHandle = SymbolHandle.Create(
            SymbolQueryService.CSharpLanguage,
            project.Id.Id.ToString("D"),
            symbol.ToDisplayString(DisplayFormat)).Format();

        var (_, slices, _) = await RenameWorkspace.PreviewRenameAsync(
            session.Solution,
            symbol,
            newName,
            cancellationToken).ConfigureAwait(false);

        var preview = new RenamePreview(
            PreviewId: Convert.ToHexString(Guid.NewGuid().ToByteArray())[..16].ToLowerInvariant(),
            Epoch: _epoch,
            ExpiresAt: DateTimeOffset.UtcNow + (ttl ?? DefaultTtl),
            OldHandle: oldHandle,
            NewName: newName,
            Documents: slices);

        _previews[preview.PreviewId] = preview;
        return preview;
    }

    public void Apply(string previewId, bool raiseAfterWrite = false, ManualWorkspaceFileWatcher? watcher = null)
    {
        if (!_previews.TryGetValue(previewId, out var preview))
        {
            throw new InvalidOperationException("unknown_preview");
        }

        if (preview.Epoch != _epoch)
        {
            throw new InvalidOperationException("epoch_mismatch");
        }

        if (preview.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("preview_expired");
        }

        var paths = preview.Documents.Select(d => d.Path).ToArray();
        using (WriteSuppression.Suppress(paths))
        {
            foreach (var slice in preview.Documents)
            {
                File.WriteAllText(slice.Path, slice.NewText, Encoding.UTF8);
                if (!_loaded.TryUpdateDocumentFromText(slice.Path, SourceText.From(slice.NewText, Encoding.UTF8)))
                {
                    throw new InvalidOperationException($"Failed to backfill '{slice.Path}'.");
                }
            }

            watcher?.Raise(paths);
            _epoch++;
        }

        _previews.Remove(previewId);

        if (raiseAfterWrite)
        {
            watcher?.Raise(paths);
        }
    }

    private static readonly SymbolDisplayFormat DisplayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.OmittedAsContaining,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions:
            SymbolDisplayMemberOptions.IncludeParameters |
            SymbolDisplayMemberOptions.IncludeContainingType |
            SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions:
            SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);
}

internal static class Observation
{
    public static void WriteJson(string name, object value)
    {
        Directory.CreateDirectory(FixturePaths.DataDir);
        var path = Path.Combine(FixturePaths.DataDir, name);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}

