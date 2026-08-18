using System.Diagnostics;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace DotNetMcp.Server;

/// <summary>
/// Process-wide MSBuildLocator registration (ported from Spike S2).
/// </summary>
public static class MsBuildBootstrap
{
    private static readonly object Gate = new();
    private static bool _registered;

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
                var sdkDir = TryFindNewestDotNetSdk();
                if (sdkDir is not null)
                {
                    MSBuildLocator.RegisterMSBuildPath(sdkDir);
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

        var parts = name.Split('-', 2)[0];
        return Version.TryParse(parts, out var v) ? v : null;
    }
}

public sealed class MsBuildSolutionLoader : ISolutionLoader
{
    public async Task<LoadedSolution> OpenAsync(
        string path,
        IProgress<LoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Workspace path not found.", fullPath);
        }

        MsBuildBootstrap.EnsureRegistered();
        var ext = Path.GetExtension(fullPath);

        if (ext.Equals(".slnf", StringComparison.OrdinalIgnoreCase))
        {
            return await OpenSlnfAsync(fullPath, progress, cancellationToken).ConfigureAwait(false);
        }

        if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return await OpenSolutionAsync(fullPath, progress, cancellationToken).ConfigureAwait(false);
        }

        if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".vbproj", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".fsproj", StringComparison.OrdinalIgnoreCase))
        {
            return await OpenProjectAsync(fullPath, progress, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidDataException(
            $"Unsupported workspace path extension '{ext}'. Use .sln, .slnx, .slnf, or a project file.");
    }

    private static MSBuildWorkspace CreateWorkspace(List<string> warnings)
    {
        // Keep Visual Basic language services in the default host catalog.
        _ = typeof(Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilation);
        var workspace = MSBuildWorkspace.Create();
#pragma warning disable CS0618
        workspace.WorkspaceFailed += (_, e) =>
        {
            warnings.Add($"{e.Diagnostic.Kind}: {e.Diagnostic.Message}");
        };
#pragma warning restore CS0618
        return workspace;
    }

    private static async Task<LoadedSolution> OpenSolutionAsync(
        string solutionPath,
        IProgress<LoadProgress>? progress,
        CancellationToken ct)
    {
        var warnings = new List<string>();
        var workspace = CreateWorkspace(warnings);
        progress?.Report(new LoadProgress(0, 1));

        var sw = Stopwatch.StartNew();
        try
        {
            var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct)
                .ConfigureAwait(false);
            sw.Stop();
            var count = Math.Max(1, solution.ProjectIds.Count);
            progress?.Report(new LoadProgress(count, count));
            return new LoadedSolution(workspace, solution, warnings);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static async Task<LoadedSolution> OpenProjectAsync(
        string projectPath,
        IProgress<LoadProgress>? progress,
        CancellationToken ct)
    {
        var warnings = new List<string>();
        var workspace = CreateWorkspace(warnings);
        progress?.Report(new LoadProgress(0, 1));

        try
        {
            _ = await workspace.OpenProjectAsync(projectPath, cancellationToken: ct)
                .ConfigureAwait(false);
            var count = Math.Max(1, workspace.CurrentSolution.ProjectIds.Count);
            progress?.Report(new LoadProgress(count, count));
            return new LoadedSolution(workspace, workspace.CurrentSolution, warnings);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static async Task<LoadedSolution> OpenSlnfAsync(
        string slnfPath,
        IProgress<LoadProgress>? progress,
        CancellationToken ct)
    {
        var projectPaths = SlnfParser.ResolveProjectPaths(slnfPath);
        var warnings = new List<string>();
        var workspace = CreateWorkspace(warnings);
        var total = Math.Max(1, projectPaths.Count);
        progress?.Report(new LoadProgress(0, total));

        try
        {
            var completed = 0;
            foreach (var projectPath in projectPaths)
            {
                ct.ThrowIfCancellationRequested();
                await workspace.OpenProjectAsync(projectPath, cancellationToken: ct)
                    .ConfigureAwait(false);
                completed++;
                progress?.Report(new LoadProgress(completed, total));
            }

            return new LoadedSolution(workspace, workspace.CurrentSolution, warnings);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }
}
