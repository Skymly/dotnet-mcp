using DotNetMcp.Core;

namespace DotNetMcp.Tests;

public class FindHitCacheTests
{
    [Fact]
    public void set_by_document_hits_same_document_and_epoch()
    {
        var cache = new FindHitCache();
        IReadOnlyList<IReadOnlyList<string>> hits = [["loc-a"], ["loc-b"]];

        cache.SetByDocument(7L, "handle", "document-scope", hits);

        Assert.True(cache.TryGetByDocument<string>(7L, "handle", "document-scope", out var found));
        Assert.Same(hits, found);
    }

    [Fact]
    public void try_get_by_document_misses_when_epoch_differs()
    {
        var cache = new FindHitCache();
        cache.SetByDocument(7L, "handle", "document-scope", new[] { new[] { "loc-a" } });

        Assert.False(cache.TryGetByDocument<string>(8L, "handle", "document-scope", out var found));
        Assert.Null(found);
    }

    [Fact]
    public void clear_then_try_get_by_document_misses()
    {
        var cache = new FindHitCache();
        cache.SetByDocument(7L, "handle", "document-scope", new[] { new[] { "loc-a" } });
        cache.Clear();

        Assert.False(cache.TryGetByDocument<string>(7L, "handle", "document-scope", out var found));
        Assert.Null(found);
    }
}
