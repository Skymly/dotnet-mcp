using DotNetMcp.Core;

namespace DotNetMcp.Tests;

public class FinderDocumentScanTests
{
    [Fact]
    public void ResolveBudget_non_positive_falls_back_to_default()
    {
        var fallback = TimeSpan.FromSeconds(5);
        Assert.Equal(fallback, FinderDocumentScan.ResolveBudget(TimeSpan.Zero, fallback));
        Assert.Equal(fallback, FinderDocumentScan.ResolveBudget(TimeSpan.FromMilliseconds(-1), fallback));
        Assert.Equal(TimeSpan.FromSeconds(2), FinderDocumentScan.ResolveBudget(TimeSpan.FromSeconds(2), fallback));
    }

    [Fact]
    public async Task ScanAsync_successful_batch_is_one_call_and_not_truncated()
    {
        var docs = new[] { "a", "b", "c" };
        var calls = new List<string[]>();

        var result = await FinderDocumentScan.ScanAsync<string, string>(
            docs,
            startIndex: 0,
            budget: TimeSpan.FromSeconds(5),
            findAligned: (scan, _) =>
            {
                calls.Add(scan.ToArray());
                return Task.FromResult<IReadOnlyList<IReadOnlyList<string>>>(
                    scan.Select(d => (IReadOnlyList<string>)[$"{d}-hit"]).ToArray());
            },
            CancellationToken.None);

        Assert.False(result.TruncatedByBudget);
        Assert.Equal(3, result.ScannedThrough);
        Assert.Equal(new[] { "a-hit" }, result.ByDocument[0]);
        Assert.Equal(new[] { "b-hit" }, result.ByDocument[1]);
        Assert.Equal(new[] { "c-hit" }, result.ByDocument[2]);
        Assert.Single(calls);
        Assert.Equal(new[] { "a", "b", "c" }, calls[0]);
    }

    [Fact]
    public async Task ScanAsync_budget_cancel_keeps_prefix_and_does_not_claim_table_end()
    {
        var docs = new[] { "a", "b", "c", "d" };
        var calls = new List<int>();

        var result = await FinderDocumentScan.ScanAsync<string, string>(
            docs,
            startIndex: 0,
            budget: TimeSpan.FromSeconds(5),
            findAligned: (scan, ct) =>
            {
                calls.Add(scan.Count);
                if (scan.Count > 1)
                {
                    throw new OperationCanceledException(ct);
                }

                return Task.FromResult<IReadOnlyList<IReadOnlyList<string>>>(
                    [(IReadOnlyList<string>)[$"{scan[0]}-hit"]]);
            },
            CancellationToken.None);

        Assert.True(result.TruncatedByBudget);
        Assert.Equal(1, result.ScannedThrough);
        Assert.Equal(new[] { "a-hit" }, result.ByDocument[0]);
        Assert.Empty(result.ByDocument[1]);
        Assert.Empty(result.ByDocument[2]);
        Assert.True(calls[0] > 1);
        Assert.Contains(1, calls);

        var (page, exhausted, nextDoc, nextLoc) = FinderDocumentScan.Slice(
            result.ByDocument, docIndex: 0, locOffset: 0, pageLimit: 50, scannedThrough: result.ScannedThrough);
        Assert.Equal(new[] { "a-hit" }, page);
        Assert.False(exhausted);
        Assert.Equal(1, nextDoc);
        Assert.Equal(0, nextLoc);
    }

    [Fact]
    public async Task ScanAsync_resume_from_breakpoint_with_enough_budget_returns_later_hits()
    {
        var docs = new[] { "a", "b", "c" };

        var first = await FinderDocumentScan.ScanAsync<string, string>(
            docs,
            startIndex: 0,
            budget: TimeSpan.FromSeconds(5),
            findAligned: (scan, ct) =>
            {
                if (scan.Count > 1)
                {
                    throw new OperationCanceledException(ct);
                }

                return Task.FromResult<IReadOnlyList<IReadOnlyList<string>>>(
                    [(IReadOnlyList<string>)[$"{scan[0]}-hit"]]);
            },
            CancellationToken.None);

        Assert.Equal(1, first.ScannedThrough);

        var second = await FinderDocumentScan.ScanAsync<string, string>(
            docs,
            startIndex: first.ScannedThrough,
            budget: TimeSpan.FromSeconds(5),
            findAligned: (scan, _) =>
            {
                return Task.FromResult<IReadOnlyList<IReadOnlyList<string>>>(
                    scan.Select(d => (IReadOnlyList<string>)[$"{d}-hit"]).ToArray());
            },
            CancellationToken.None);

        Assert.False(second.TruncatedByBudget);
        Assert.Equal(3, second.ScannedThrough);
        var (page, exhausted, nextDoc, _) = FinderDocumentScan.Slice(
            second.ByDocument,
            docIndex: first.ScannedThrough,
            locOffset: 0,
            pageLimit: 50,
            scannedThrough: second.ScannedThrough);
        Assert.Equal(new[] { "b-hit", "c-hit" }, page);
        Assert.True(exhausted);
        Assert.Equal(3, nextDoc);
    }

    [Fact]
    public void Slice_unscanned_tail_is_not_exhausted()
    {
        IReadOnlyList<IReadOnlyList<string>> byDocument =
        [
            ["a"],
            [],
            [],
        ];

        var (page, exhausted, nextDoc, nextLoc) = FinderDocumentScan.Slice(
            byDocument, docIndex: 0, locOffset: 0, pageLimit: 50, scannedThrough: 1);

        Assert.Equal(new[] { "a" }, page);
        Assert.False(exhausted);
        Assert.Equal(1, nextDoc);
        Assert.Equal(0, nextLoc);
    }
}
