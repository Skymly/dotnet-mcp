using System.Diagnostics;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace S2.Core;

public static class MsBuildBootstrap
{
    private static readonly object Gate = new();
    private static bool _registered;
    private static string? _msbuildPath;
    private static string? _msbuildVersion;
    private static string? _discoveryType;

    public static string? MsBuildPath => _msbuildPath;
    public static string? MsBuildVersion => _msbuildVersion;
    public static string? DiscoveryTypeName => _discoveryType;

    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            if (!MSBuildLocator.IsRegistered)
            {
                // MSBuildLocator's DotNetSdk discovery can miss newer SDKs when the host TFM is older
                // (e.g. net8.0 process may only surface 8.x). Prefer the newest folder under the
                // installed dotnet root, then fall back to QueryVisualStudioInstances.
                var sdkDir = TryFindNewestDotNetSdk();
                if (sdkDir is not null)
                {
                    MSBuildLocator.RegisterMSBuildPath(sdkDir);
                    _msbuildPath = sdkDir;
                    _msbuildVersion = new DirectoryInfo(sdkDir).Name;
                    _discoveryType = "DotNetSdkFolder";
                }
                else
                {
                    var instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
                    var chosen = instances
                        .Where(i => i.DiscoveryType == DiscoveryType.DotNetSdk)
                        .OrderByDescending(i => i.Version)
                        .FirstOrDefault()
                        ?? instances.OrderByDescending(i => i.Version).FirstOrDefault()
                        ?? throw new InvalidOperationException("No MSBuild instances found via MSBuildLocator.");
                    MSBuildLocator.RegisterInstance(chosen);
                    _msbuildPath = chosen.MSBuildPath;
                    _msbuildVersion = chosen.Version.ToString();
                    _discoveryType = chosen.DiscoveryType.ToString();
                }
            }

            _registered = true;
        }
    }

    private static string? TryFindNewestDotNetSdk()
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"),
            @"C:\Program Files\dotnet",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
        };

        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var sdkRoot = Path.Combine(root!, "sdk");
            if (!Directory.Exists(sdkRoot))
            {
                continue;
            }

            var newest = Directory.GetDirectories(sdkRoot)
                .Select(d => new { Path = d, Name = Path.GetFileName(d), Version = ParseSdkVersion(Path.GetFileName(d)) })
                .Where(x => x.Version is not null)
                .OrderByDescending(x => x.Version)
                .FirstOrDefault();

            if (newest is not null && File.Exists(Path.Combine(newest.Path, "MSBuild.dll")))
            {
                return newest.Path;
            }
        }

        return null;
    }

    private static Version? ParseSdkVersion(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // e.g. 10.0.302 or 8.0.423
        var parts = name.Split('-', 2)[0];
        return Version.TryParse(parts, out var v) ? v : null;
    }
}

public sealed class WorkspaceDiagnosticRecord
{
    public required string Kind { get; init; }
    public required string Message { get; init; }
}

public sealed class LoadedWorkspace : IAsyncDisposable
{
    private readonly MSBuildWorkspace _workspace;

    public LoadedWorkspace(
        MSBuildWorkspace workspace,
        Solution solution,
        IReadOnlyList<WorkspaceDiagnosticRecord> diagnostics,
        TimeSpan loadElapsed,
        long peakWorkingSetBytes)
    {
        _workspace = workspace;
        Solution = solution;
        Diagnostics = diagnostics;
        LoadElapsed = loadElapsed;
        PeakWorkingSetBytes = peakWorkingSetBytes;
    }

    public Solution Solution { get; }
    public IReadOnlyList<WorkspaceDiagnosticRecord> Diagnostics { get; }
    public TimeSpan LoadElapsed { get; }
    public long PeakWorkingSetBytes { get; }

    public ValueTask DisposeAsync()
    {
        _workspace.Dispose();
        return ValueTask.CompletedTask;
    }
}

public static class SolutionLoader
{
    private static MSBuildWorkspace CreateWorkspace(List<WorkspaceDiagnosticRecord> diagnostics)
    {
        var workspace = MSBuildWorkspace.Create();
#pragma warning disable CS0618
        workspace.WorkspaceFailed += (_, e) =>
        {
            diagnostics.Add(new WorkspaceDiagnosticRecord
            {
                Kind = e.Diagnostic.Kind.ToString(),
                Message = e.Diagnostic.Message,
            });
        };
#pragma warning restore CS0618
        return workspace;
    }

    public static async Task<LoadedWorkspace> OpenSolutionAsync(
        string solutionPath,
        CancellationToken ct = default)
    {
        MsBuildBootstrap.EnsureRegistered();
        var diagnostics = new List<WorkspaceDiagnosticRecord>();
        var workspace = CreateWorkspace(diagnostics);

        using var metrics = ProcessMetrics.Start();
        var sw = Stopwatch.StartNew();
        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);
        sw.Stop();
        metrics.Sample();

        return new LoadedWorkspace(workspace, solution, diagnostics, sw.Elapsed, metrics.PeakWorkingSetBytes);
    }

    /// <summary>
    /// Opens projects listed in a .slnf by self-parsing the filter and calling OpenProjectAsync
    /// for each path (public API does not support .slnf).
    /// </summary>
    public static async Task<LoadedWorkspace> OpenSlnfAsync(
        string slnfPath,
        CancellationToken ct = default)
    {
        MsBuildBootstrap.EnsureRegistered();
        var projectPaths = SlnfParser.ResolveProjectPaths(slnfPath);

        var diagnostics = new List<WorkspaceDiagnosticRecord>();
        var workspace = CreateWorkspace(diagnostics);

        using var metrics = ProcessMetrics.Start();
        var sw = Stopwatch.StartNew();

        foreach (var projectPath in projectPaths)
        {
            ct.ThrowIfCancellationRequested();
            await workspace.OpenProjectAsync(projectPath, cancellationToken: ct);
            metrics.Sample();
        }

        sw.Stop();
        metrics.Sample();

        return new LoadedWorkspace(
            workspace,
            workspace.CurrentSolution,
            diagnostics,
            sw.Elapsed,
            metrics.PeakWorkingSetBytes);
    }

    public static int CleanBinObj(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return 0;
        }

        var targets = Directory.EnumerateDirectories(rootDirectory, "*", SearchOption.AllDirectories)
            .Where(dir =>
            {
                var name = Path.GetFileName(dir);
                return string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase);
            })
            // deepest first so parents aren't partially deleted while enumerating children
            .OrderByDescending(d => d.Length)
            .ToArray();

        var removed = 0;
        foreach (var dir in targets)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                    removed++;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to delete {dir}: {ex.Message}");
            }
        }

        return removed;
    }
}
