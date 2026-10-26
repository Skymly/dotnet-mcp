using System.Reflection;
using System.Runtime.Loader;
using DotNetMcp.Server;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Tests;

/// <summary>
/// Issue #549: analyzer references become non-locking byte-loads at load time and are
/// re-read when a write advances the epoch, so a rebuilt in-workspace generator cannot
/// keep producing stale artifacts for later diagnostics.
/// </summary>
public class AnalyzerRefreshSeamTests
{
    [Fact]
    public async Task apply_epoch_advance_refreshes_changed_analyzer_and_diagnostics_follow()
    {
        var root = CreateTempDir("analyzer-refresh");
        var projectDir = Path.Combine(root, "proj");
        var analyzerDir = Path.Combine(root, "analyzers");
        Directory.CreateDirectory(analyzerDir);
        var analyzerPath = Path.Combine(analyzerDir, "CustomGenerator.dll");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        File.Copy(typeof(CustomGenerator.MarkerGenerator).Assembly.Location, analyzerPath);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithAnalyzerRefreshOnDisk(projectDir, analyzerPath));

            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);
            var projectId = await GetSingleProjectIdAsync(fx);

            var before = await fx.Client.CallToolAsync(
                "project_diagnostics",
                new Dictionary<string, object?> { ["projectId"] = projectId });
            Assert.True(before.IsError is not true, InProcessMcpFixture.TextOf(before));
            var beforeBody = InProcessMcpFixture.Deserialize<ProjectDiagnosticsResultDto>(before);
            Assert.DoesNotContain(beforeBody.Items,
                d => d.FilePath is not null && d.FilePath.EndsWith("Consumer.cs", StringComparison.Ordinal));

            // Materializing generators is what used to pin the DLL under the default
            // memory-mapping loader; it must stay loadable AND overwritable.
            var list = await fx.Client.CallToolAsync(
                "project_list_generators",
                new Dictionary<string, object?> { ["projectId"] = projectId });
            Assert.True(list.IsError is not true, InProcessMcpFixture.TextOf(list));
            var listBody = InProcessMcpFixture.Deserialize<ProjectListGeneratorsResultDto>(list);
            Assert.Contains(listBody.Generators,
                g => g.TypeFullName == "CustomGenerator.MarkerGenerator");

            // User-side rebuild stand-in: same path, different analyzer bytes (a dll with
            // no generators — like a rebuilt generator project whose output moved). With
            // the old lazy AnalyzerFileReference this overwrite would collide with the
            // memory-mapped (locked) DLL on Windows.
            File.Copy(typeof(DotNetMcp.Core.GeneratorDriverRunner).Assembly.Location,
                analyzerPath, overwrite: true);

            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "GenHost.Renamable.Ping" });
            Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
            var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;

            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["newName"] = "Pong"
                });
            Assert.True(preview.IsError is not true, InProcessMcpFixture.TextOf(preview));
            var previewId = InProcessMcpFixture.Deserialize<SymbolPreviewRenameResultDto>(preview).PreviewId;

            var epochBefore = fx.WorkspaceHost.CurrentEpoch;
            var apply = await fx.Client.CallToolAsync(
                "symbol_apply_rename",
                new Dictionary<string, object?> { ["previewId"] = previewId });
            Assert.True(apply.IsError is not true, InProcessMcpFixture.TextOf(apply));
            Assert.True(fx.WorkspaceHost.CurrentEpoch > epochBefore);

            var listAfter = await fx.Client.CallToolAsync(
                "project_list_generators",
                new Dictionary<string, object?> { ["projectId"] = projectId });
            Assert.True(listAfter.IsError is not true, InProcessMcpFixture.TextOf(listAfter));
            var afterGens = InProcessMcpFixture.Deserialize<ProjectListGeneratorsResultDto>(listAfter);
            Assert.DoesNotContain(afterGens.Generators,
                g => g.TypeFullName == "CustomGenerator.MarkerGenerator");

            var after = await fx.Client.CallToolAsync(
                "project_diagnostics",
                new Dictionary<string, object?> { ["projectId"] = projectId });
            Assert.True(after.IsError is not true, InProcessMcpFixture.TextOf(after));
            var afterBody = InProcessMcpFixture.Deserialize<ProjectDiagnosticsResultDto>(after);
            // The refreshed analyzer no longer emits SampleApp.Generated.CustomMarker —
            // the stale artifact is gone from the compilation instead of hanging around,
            // so the handwritten reference now reports CS0103 (namespace gone).
            Assert.Contains(afterBody.Items,
                d => d.Id == "CS0103" && d.Message.Contains("SampleApp", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task refresh_plan_is_empty_when_analyzer_bytes_are_unchanged()
    {
        var root = CreateTempDir("analyzer-unchanged");
        var projectDir = Path.Combine(root, "proj");
        var analyzerDir = Path.Combine(root, "analyzers");
        Directory.CreateDirectory(analyzerDir);
        var analyzerPath = Path.Combine(analyzerDir, "CustomGenerator.dll");
        File.Copy(typeof(CustomGenerator.MarkerGenerator).Assembly.Location, analyzerPath);

        try
        {
            await using var loaded = FakeSolutionLoader.CreateAnalyzerRefreshLoadedOnDisk(
                projectDir, analyzerPath);
            Assert.Equal(1, loaded.ConvertToNonLockingAnalyzerReferences(static _ => true));

            // Unchanged bytes -> no plan, no reference churn.
            Assert.Null(loaded.PlanAnalyzerReferenceRefresh(static _ => true));

            var referenceBefore = loaded.Solution.Projects.Single().AnalyzerReferences.Single();
            File.WriteAllBytes(analyzerPath,
                File.ReadAllBytes(typeof(DotNetMcp.Core.GeneratorDriverRunner).Assembly.Location));
            var plan = loaded.PlanAnalyzerReferenceRefresh(static _ => true);
            Assert.NotNull(plan);
            loaded.ApplyAnalyzerReferenceRefresh(plan!);

            var referenceAfter = loaded.Solution.Projects.Single().AnalyzerReferences.Single();
            Assert.NotSame(referenceBefore, referenceAfter);

            // Second pass with the same bytes plans nothing again.
            Assert.Null(loaded.PlanAnalyzerReferenceRefresh(static _ => true));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// Windows locks memory-mapped assemblies; the byte-loading
    /// <see cref="NonLockingAnalyzerLoader"/> must not. The first half of the test pins
    /// down the premise (a mapping <see cref="AssemblyLoadContext"/> blocks overwrite),
    /// the second half proves the converted workspace reference does not.
    /// </summary>
    [WindowsFact]
    public async Task converted_analyzer_reference_keeps_dll_overwritable_while_loaded()
    {
        var root = CreateTempDir("analyzer-lock");
        var projectDir = Path.Combine(root, "proj");
        var analyzerDir = Path.Combine(root, "analyzers");
        Directory.CreateDirectory(analyzerDir);
        var analyzerPath = Path.Combine(analyzerDir, "CustomGenerator.dll");
        File.Copy(typeof(CustomGenerator.MarkerGenerator).Assembly.Location, analyzerPath);

        try
        {
            // Premise on a sibling copy: a plain mapped AssemblyLoadContext load keeps
            // the DLL locked for as long as the context lives.
            var lockedCopy = Path.Combine(analyzerDir, "CustomGenerator-mapped.dll");
            File.Copy(analyzerPath, lockedCopy);
            var mappingContext = new AssemblyLoadContext("lock-contrast", isCollectible: true);
            mappingContext.LoadFromAssemblyPath(lockedCopy);
            Assert.Throws<IOException>(() =>
                File.WriteAllBytes(lockedCopy, [0x4D, 0x5A]));

            await using var loaded = FakeSolutionLoader.CreateAnalyzerRefreshLoadedOnDisk(
                projectDir, analyzerPath);
            Assert.Equal(1, loaded.ConvertToNonLockingAnalyzerReferences(static _ => true));

            // Force materialization: generators enumerated → assembly byte-loaded into the
            // collectible context. The analyzer file must still accept an overwrite.
            var generators = loaded.Solution.Projects
                .SelectMany(p => p.AnalyzerReferences)
                .SelectMany(r => r.GetGenerators(LanguageNames.CSharp))
                .ToArray();
            Assert.Contains(generators,
                g => g.GetGeneratorType().FullName == "CustomGenerator.MarkerGenerator");

            var freshBytes = File.ReadAllBytes(
                typeof(DotNetMcp.Core.GeneratorDriverRunner).Assembly.Location);
            File.WriteAllBytes(analyzerPath, freshBytes);
            Assert.Equal(freshBytes.Length, new FileInfo(analyzerPath).Length);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<string> GetSingleProjectIdAsync(InProcessMcpFixture fx)
    {
        var list = await fx.Client.CallToolAsync(
            "workspace_list_projects",
            new Dictionary<string, object?>());
        Assert.True(list.IsError is not true, InProcessMcpFixture.TextOf(list));
        var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list);
        return Assert.Single(projects.Projects).ProjectId;
    }

    private static string CreateTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnet-mcp-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
