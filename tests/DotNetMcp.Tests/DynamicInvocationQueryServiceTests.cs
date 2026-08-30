using DotNetMcp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

public class DynamicInvocationQueryServiceTests
{
    [Fact]
    public async Task list_async_returns_page_of_dynamic_invocation_items()
    {
        using var workspace = CreateDynamicWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var projectId = ProjectIdOf(workspace);

        var (page, error) = await new DynamicInvocationQueryService().ListAsync(session, projectId);

        Assert.Null(error);
        Assert.NotNull(page);
        Assert.Contains(page!.Items, i => i.Kind is "Invocation" or "Member");
        Assert.False(page.Truncated);
    }

    [Fact]
    public async Task list_async_unknown_project_is_project_not_found()
    {
        using var workspace = CreateDynamicWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var (_, error) = await new DynamicInvocationQueryService().ListAsync(session, "missing");
        Assert.IsType<ProjectNotFoundError>(error);
    }

    [Fact]
    public async Task list_async_fsharp_project_is_project_not_found()
    {
        var workspace = new AdhocWorkspace();
        workspace.AddProject("FsLib", LanguageNames.FSharp);
        using var session = new FakeSession(workspace.CurrentSolution);
        var projectId = workspace.CurrentSolution.Projects.Single().Id.Id.ToString("D");
        var (_, error) = await new DynamicInvocationQueryService().ListAsync(session, projectId);
        Assert.IsType<ProjectNotFoundError>(error);
    }

    [Fact]
    public async Task list_async_unavailable_compilation_is_compilation_unavailable()
    {
        using var workspace = CreateDynamicWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution, compilationUnavailable: true);
        var (_, error) = await new DynamicInvocationQueryService().ListAsync(session, ProjectIdOf(workspace));
        Assert.IsType<CompilationUnavailableError>(error);
    }

    private static string ProjectIdOf(AdhocWorkspace workspace) =>
        workspace.CurrentSolution.Projects.Single().Id.Id.ToString("D");

    private static AdhocWorkspace CreateDynamicWorkspace()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        const string source = """
            namespace DynLib;
            public static class Host
            {
                public static object Run(dynamic d)
                {
                    var a = d.Foo(1, "x");
                    return a;
                }
            }
            """;
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "DynLib",
            "DynLib",
            LanguageNames.CSharp,
            filePath: @"C:\fake\DynLib.csproj"));
        solution = solution.AddDocument(
            DocumentId.CreateNewId(projectId),
            "Host.cs",
            SourceText.From(source),
            filePath: @"C:\fake\Host.cs");
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(
            projectId,
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        var csharp = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.CSharp");
        if (csharp is { Location.Length: > 0 })
        {
            solution = solution.AddMetadataReference(projectId, MetadataReference.CreateFromFile(csharp.Location));
        }

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace.");
        }

        return workspace;
    }

    private sealed class FakeSession : IWorkspaceSession
    {
        private readonly bool _compilationUnavailable;

        public FakeSession(Solution solution, bool compilationUnavailable = false)
        {
            Solution = solution;
            Epoch = 1;
            FSharpSnapshot = new FSharpWorkspaceSnapshot(1, []);
            _compilationUnavailable = compilationUnavailable;
        }

        public long Epoch { get; }

        public Solution Solution { get; }

        public FSharpWorkspaceSnapshot FSharpSnapshot { get; }

        public async Task<Compilation> GetCompilationAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default)
        {
            if (_compilationUnavailable)
            {
                throw new InvalidOperationException("Compilation was null.");
            }

            var project = Solution.GetProject(projectId)
                ?? throw new InvalidOperationException($"Project '{projectId.Id}' is not in the session solution.");
            return await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Compilation was null for project '{project.Name}'.");
        }

        public Task<Compilation> GetCompilationWithoutGeneratedTreesAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DriverRunSnapshot> GetGeneratorRunResultAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
