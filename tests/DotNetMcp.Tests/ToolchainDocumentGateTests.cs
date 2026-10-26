using DotNetMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

/// <summary>
/// ADR-0004 Amendment 10 (#544): the loaded-graph document set may include read-only
/// files under toolchain roots — packages like xunit v3 inject package-cache sources
/// (<c>_content/*.cs</c>) into the compile. Project paths and the write/apply set stay
/// trusted-roots-only. <c>NUGET_PACKAGES</c> is set in-process to simulate the package
/// cache; the test assembly does not parallelize, so the process env var is safe.
/// </summary>
public class ToolchainDocumentGateTests
{
    [Fact]
    public void loaded_graph_document_under_nuget_packages_is_accepted()
    {
        var root = CreateTempDir("root");
        var packages = CreateTempDir("pkgs");
        var loaded = LoadedWithPackageDocument(root, packages, out _);
        var previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");

        try
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", packages);

            TrustedGraphGate.EnsureLoadedSolutionUnderRoots(loaded, TrustedRoots.Create([root]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", previous);
            TryDelete(root);
            TryDelete(packages);
        }
    }

    [Fact]
    public void loaded_graph_additional_document_under_toolchain_root_is_accepted()
    {
        var root = CreateTempDir("root");
        var packages = CreateTempDir("pkgs");
        var insideProj = Path.Combine(root, "App.csproj");
        File.WriteAllText(insideProj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var packageFile = Path.Combine(packages, "some.pkg", "1.0.0", "contentFiles", "ruleset.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(packageFile)!);
        File.WriteAllText(packageFile, "package-shipped rules");

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var additionalId = DocumentId.CreateNewId(projectId);
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "App",
            "App",
            LanguageNames.CSharp,
            filePath: insideProj));
        solution = solution.AddAdditionalDocument(
            additionalId,
            "ruleset.txt",
            SourceText.From("package-shipped rules"),
            filePath: packageFile);
        Assert.True(workspace.TryApplyChanges(solution));

        var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, []);
        var previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");

        try
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", packages);

            TrustedGraphGate.EnsureLoadedSolutionUnderRoots(loaded, TrustedRoots.Create([root]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", previous);
            TryDelete(root);
            TryDelete(packages);
        }
    }

    [Fact]
    public void loaded_graph_document_outside_both_roots_fails_naming_toolchain_roots()
    {
        var root = CreateTempDir("root");
        var packages = CreateTempDir("pkgs");
        var outside = CreateTempDir("outside");
        var insideProj = Path.Combine(root, "App.csproj");
        File.WriteAllText(insideProj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var outsideDoc = Path.Combine(outside, "Evil.cs");
        File.WriteAllText(outsideDoc, "namespace Evil; public class Leak {}");

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "App",
            "App",
            LanguageNames.CSharp,
            filePath: insideProj));
        solution = solution.AddDocument(
            docId,
            "Evil.cs",
            SourceText.From("namespace Evil; public class Leak {}"),
            filePath: outsideDoc);
        Assert.True(workspace.TryApplyChanges(solution));

        var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, []);
        var previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");

        try
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", packages);

            var ex = Assert.Throws<LoadedGraphOutsideTrustedRootsException>(
                () => TrustedGraphGate.EnsureLoadedSolutionUnderRoots(loaded, TrustedRoots.Create([root])));
            Assert.Contains("toolchain roots", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", previous);
            TryDelete(root);
            TryDelete(packages);
            TryDelete(outside);
        }
    }

    [Fact]
    public void loaded_graph_project_under_toolchain_root_is_still_refused()
    {
        var root = CreateTempDir("root");
        var packages = CreateTempDir("pkgs");
        var packageProj = Path.Combine(packages, "some.pkg", "1.0.0", "lib", "Evil.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(packageProj)!);
        File.WriteAllText(packageProj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var workspace = new AdhocWorkspace();
        workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "Evil",
            "Evil",
            LanguageNames.CSharp,
            filePath: packageProj));

        var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, []);
        var previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");

        try
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", packages);

            // A project inside the package cache is not a supported scenario: project
            // paths stay strict even though documents/analyzers may use toolchain roots.
            var ex = Assert.Throws<LoadedGraphOutsideTrustedRootsException>(
                () => TrustedGraphGate.EnsureLoadedSolutionUnderRoots(loaded, TrustedRoots.Create([root])));
            Assert.Contains("project path", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("toolchain", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", previous);
            TryDelete(root);
            TryDelete(packages);
        }
    }

    [Fact]
    public void loaded_graph_analyzer_under_toolchain_root_is_accepted()
    {
        var root = CreateTempDir("root");
        var packages = CreateTempDir("pkgs");
        var insideProj = Path.Combine(root, "App.csproj");
        File.WriteAllText(insideProj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var dllSrc = Path.Combine(AppContext.BaseDirectory, "CustomGenerator.dll");
        Assert.True(File.Exists(dllSrc), $"Missing CustomGenerator.dll next to tests: {dllSrc}");
        var packageDll = Path.Combine(packages, "some.pkg", "1.0.0", "analyzers", "dotnet", "cs", "CustomGenerator.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(packageDll)!);
        File.Copy(dllSrc, packageDll);

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        workspace.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "App",
            "App",
            LanguageNames.CSharp,
            filePath: insideProj));
        var solution = workspace.CurrentSolution.AddAnalyzerReference(
            projectId,
            new AnalyzerFileReference(packageDll, PassThroughAnalyzerAssemblyLoader.Instance));
        Assert.True(workspace.TryApplyChanges(solution));

        var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, []);
        var previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");

        try
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", packages);

            TrustedGraphGate.EnsureLoadedSolutionUnderRoots(loaded, TrustedRoots.Create([root]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", previous);
            TryDelete(root);
            TryDelete(packages);
        }
    }

    [Fact]
    public async Task workspace_open_with_package_cache_document_reaches_ready()
    {
        var root = CreateTempDir("root");
        var packages = CreateTempDir("pkgs");
        var loaded = LoadedWithPackageDocument(root, packages, out var packageDoc);
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        var previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");

        try
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", packages);

            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                new FakeSolutionLoader(TimeSpan.Zero, () => loaded));

            var status = await WorkspaceReady.OpenUntilReadyAsync(fx, solution);
            Assert.Equal("ready", status.Phase);

            // Matching on-disk content is quiet: a toolchain-root document is an admitted
            // read-only member, not a permanent OutsideTrustedRoots drift flag.
            var drift = await fx.Client.CallToolAsync(
                "workspace_check_drift",
                new Dictionary<string, object?>());
            Assert.True(drift.IsError is not true, InProcessMcpFixture.TextOf(drift));
            var body = InProcessMcpFixture.Deserialize<WorkspaceCheckDriftResultDto>(drift);
            Assert.DoesNotContain(
                body.Drifted,
                d => d.Path.Contains("DefaultRunnerReporters.cs", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", previous);
            TryDelete(root);
            TryDelete(packages);
        }
    }

    [Fact]
    public async Task write_declared_paths_refuses_toolchain_root_document()
    {
        var root = CreateTempDir("root");
        var packages = CreateTempDir("pkgs");
        var loaded = LoadedWithPackageDocument(root, packages, out var packageDoc);
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        var diskText = await File.ReadAllTextAsync(packageDoc);
        var previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");

        try
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", packages);

            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                new FakeSolutionLoader(TimeSpan.Zero, () => loaded));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            // Fixture sanity: the path really is toolchain content, yet still unwritable.
            Assert.True(ToolchainRoots.Discover().Contains(packageDoc));

            var outcome = fx.WorkspaceHost.WriteDeclaredPaths(
                [new WorkspaceEditDocument(packageDoc, diskText, diskText + "//tampered")]);

            Assert.True(outcome.Failed);
            Assert.Equal(PolicyErrorCodes.PathOutsideTrustedRoots, outcome.Error!.Error);
            Assert.Equal(diskText, await File.ReadAllTextAsync(packageDoc));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", previous);
            TryDelete(root);
            TryDelete(packages);
        }
    }

    [Fact]
    public void preview_refuses_toolchain_root_path()
    {
        var root = CreateTempDir("root");
        var packages = CreateTempDir("pkgs");
        var packageDoc = Path.Combine(packages, "some.pkg", "1.0.0", "_content", "Injected.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(packageDoc)!);
        File.WriteAllText(packageDoc, "namespace Injected;");
        var previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");

        try
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", packages);
            Assert.True(ToolchainRoots.Discover().Contains(packageDoc));

            var edits = new WorkspaceEdit(
                new RefusingWriter(),
                TrustedRoots.Create([root]),
                TimeProvider.System,
                TimeSpan.FromMinutes(5));

            var outcome = edits.Preview(new WorkspaceEditDraft(
                WorkspaceEditKind.RenamePreview,
                [new WorkspaceEditDocument(packageDoc, "old", "new")],
                []));

            Assert.True(outcome.Failed);
            Assert.Equal(PolicyErrorCodes.PreviewPathOutsideTrustedRoots, outcome.Error!.Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", previous);
            TryDelete(root);
            TryDelete(packages);
        }
    }

    [Fact]
    public async Task check_drift_flags_toolchain_document_mismatch_without_repair()
    {
        var root = CreateTempDir("root");
        var packages = CreateTempDir("pkgs");
        var loaded = LoadedWithPackageDocument(root, packages, out var packageDoc);
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        var previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");

        try
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", packages);

            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                new FakeSolutionLoader(TimeSpan.Zero, () => loaded));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            // Diverge the package-cache file: detection may read it (the gate admitted it
            // as a read-only member), but repair stays trusted-roots-only — flagged,
            // never Repaired, and the workspace snapshot keeps the load-time text.
            await File.WriteAllTextAsync(packageDoc, "namespace Injected; public static class Changed {}");

            var drift = await fx.Client.CallToolAsync(
                "workspace_check_drift",
                new Dictionary<string, object?>());
            Assert.True(drift.IsError is not true, InProcessMcpFixture.TextOf(drift));
            var body = InProcessMcpFixture.Deserialize<WorkspaceCheckDriftResultDto>(drift);
            var flagged = Assert.Single(
                body.Drifted,
                d => d.Path.Contains("DefaultRunnerReporters.cs", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("ContentMismatch", flagged.Kind);
            Assert.False(flagged.Repaired);

            Assert.True(fx.WorkspaceHost.TryGetReadySession(out var session));
            var documentId = Assert.Single(session!.Solution.GetDocumentIdsWithFilePath(packageDoc));
            var text = (await session.Solution.GetDocument(documentId)!.GetTextAsync()).ToString();
            Assert.Contains("Reporters", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Changed", text, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", previous);
            TryDelete(root);
            TryDelete(packages);
        }
    }

    /// <summary>
    /// Mirrors issue #544: a project under <paramref name="root"/> compiles a source file
    /// injected from the NuGet package cache (xunit v3's DefaultRunnerReporters.cs).
    /// </summary>
    private static LoadedSolution LoadedWithPackageDocument(
        string root,
        string packages,
        out string packageDoc)
    {
        var insideProj = Path.Combine(root, "App.csproj");
        File.WriteAllText(insideProj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        packageDoc = Path.Combine(
            packages,
            "xunit.v3.core.mtp-v1",
            "3.2.2",
            "_content",
            "DefaultRunnerReporters.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(packageDoc)!);
        const string text = "namespace Injected; public static class Reporters {}";
        File.WriteAllText(packageDoc, text);

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "App",
            "App",
            LanguageNames.CSharp,
            filePath: insideProj));
        solution = solution.AddDocument(
            docId,
            "DefaultRunnerReporters.cs",
            SourceText.From(text),
            filePath: packageDoc);
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply package-document fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, []);
    }

    private static string CreateTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnet-mcp-tcd-{label}-{Guid.NewGuid():N}");
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

    private sealed class PassThroughAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
    {
        public static PassThroughAnalyzerAssemblyLoader Instance { get; } = new();

        public void AddDependencyLocation(string fullPath)
        {
        }

        public System.Reflection.Assembly LoadFromPath(string fullPath) =>
            System.Reflection.Assembly.LoadFrom(fullPath);
    }

    private sealed class RefusingWriter : IWorkspaceEditWriter
    {
        public long CurrentEpoch => 1;

        public long Generation => 1;

        public bool PathExists(string path) => File.Exists(path);

        public WorkspaceEditOutcome<long> WriteDeclaredPaths(
            IReadOnlyList<WorkspaceEditDocument> documents) =>
            throw new InvalidOperationException("Write must not be reached for a refused preview.");
    }
}
