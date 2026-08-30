using DotNetMcp.Core;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Tests;

public class CompilationLruTests
{
    [Fact]
    public async Task concurrent_get_or_add_same_project_returns_one_cached_compilation()
    {
        using var workspace = CreateWorkspace(out var project);
        var lru = new CompilationLru(50);
        using var started = new CountdownEvent(8);
        var release = new TaskCompletionSource();
        var n = 0;

        async Task<Compilation> DistinctFactory(Project p, CancellationToken ct)
        {
            started.Signal();
            await release.Task.WaitAsync(ct);
            var compilation = await p.GetCompilationAsync(ct)
                ?? throw new InvalidOperationException("Compilation was null.");
            return compilation.WithAssemblyName(compilation.AssemblyName + Interlocked.Increment(ref n));
        }

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => lru.GetOrAddAsync(project, DistinctFactory, CancellationToken.None))
            .ToArray();

        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        release.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, lru.Count);
        Assert.All(results, c => Assert.Same(results[0], c));
        Assert.True(lru.TryGet(project.Id, out var cached));
        Assert.Same(results[0], cached);

        var extraFactories = 0;
        Task<Compilation> ExtraFactory(Project p, CancellationToken ct)
        {
            Interlocked.Increment(ref extraFactories);
            return p.GetCompilationAsync(ct)!;
        }

        var again = await lru.GetOrAddAsync(project, ExtraFactory, CancellationToken.None);
        Assert.Same(results[0], again);
        Assert.Equal(0, extraFactories);
        Assert.True(lru.Hits >= 1);
    }

    [Fact]
    public async Task second_get_or_add_same_project_counts_as_hit_not_factory()
    {
        using var workspace = CreateWorkspace(out var project);
        var lru = new CompilationLru(50);
        var factories = 0;

        Task<Compilation> Factory(Project p, CancellationToken ct)
        {
            Interlocked.Increment(ref factories);
            return p.GetCompilationAsync(ct)!;
        }

        var first = await lru.GetOrAddAsync(project, Factory, CancellationToken.None);
        Assert.True(lru.TryGet(project.Id, out var cached));
        Assert.Same(first, cached);

        var second = await lru.GetOrAddAsync(project, Factory, CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, factories);
        Assert.Equal(1, lru.Misses);
        Assert.Equal(1, lru.Hits);
    }

    private static AdhocWorkspace CreateWorkspace(out Project project)
    {
        var workspace = new AdhocWorkspace();
        project = workspace.AddProject("SampleLib", LanguageNames.CSharp);
        return workspace;
    }
}
