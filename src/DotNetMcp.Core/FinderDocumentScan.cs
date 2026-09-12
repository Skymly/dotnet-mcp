namespace DotNetMcp.Core;

/// <summary>
/// Finder scan: one call over the remaining documents on the happy path.
/// Budget cancellation does not treat the table as exhausted; it keeps the
/// next cursor on the first unfinished document.
/// </summary>
internal static class FinderDocumentScan
{
    public static TimeSpan ResolveBudget(TimeSpan requested, TimeSpan defaultBudget) =>
        requested <= TimeSpan.Zero ? defaultBudget : requested;

    public static async Task<Result<THit>> ScanAsync<TDoc, THit>(
        IReadOnlyList<TDoc> documents,
        int startIndex,
        TimeSpan budget,
        Func<IReadOnlyList<TDoc>, CancellationToken, Task<IReadOnlyList<IReadOnlyList<THit>>>> findAligned,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(findAligned);
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startIndex, documents.Count);

        var filled = new IReadOnlyList<THit>[documents.Count];
        for (var i = 0; i < filled.Length; i++)
        {
            filled[i] = Array.Empty<THit>();
        }

        if (startIndex == documents.Count)
        {
            return new Result<THit>(filled, documents.Count, TruncatedByBudget: false);
        }

        var remaining = new TDoc[documents.Count - startIndex];
        for (var i = 0; i < remaining.Length; i++)
        {
            remaining[i] = documents[startIndex + i];
        }

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (budget <= TimeSpan.Zero)
        {
            budgetCts.Cancel();
        }
        else
        {
            budgetCts.CancelAfter(budget);
        }

        if (!budgetCts.IsCancellationRequested)
        {
            try
            {
                var aligned = await findAligned(remaining, budgetCts.Token).ConfigureAwait(false);
                Fill(filled, startIndex, aligned);
                return new Result<THit>(filled, documents.Count, TruncatedByBudget: false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Batch cancelled: scan the first remaining document without the budget
                // token so the cursor can advance instead of pointing at the table tail.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var one = await findAligned([documents[startIndex]], cancellationToken).ConfigureAwait(false);
        filled[startIndex] = one.Count > 0 ? one[0] : Array.Empty<THit>();
        var scannedThrough = startIndex + 1;
        return new Result<THit>(filled, scannedThrough, TruncatedByBudget: scannedThrough < documents.Count);
    }

    public static (List<THit> Page, bool Exhausted, int NextDoc, int NextLoc) Slice<THit>(
        IReadOnlyList<IReadOnlyList<THit>> byDocument,
        int docIndex,
        int locOffset,
        int pageLimit,
        int scannedThrough)
    {
        ArgumentNullException.ThrowIfNull(byDocument);
        var page = new List<THit>();
        var limit = Math.Max(1, pageLimit);
        var end = Math.Min(Math.Max(scannedThrough, 0), byDocument.Count);
        for (var i = docIndex; i < end; i++)
        {
            var hits = byDocument[i];
            var start = i == docIndex ? locOffset : 0;
            for (var loc = start; loc < hits.Count; loc++)
            {
                if (page.Count >= limit)
                {
                    return (page, false, i, loc);
                }

                page.Add(hits[loc]);
            }
        }

        if (end < byDocument.Count)
        {
            return (page, false, end, 0);
        }

        return (page, true, byDocument.Count, 0);
    }

    private static void Fill<THit>(IReadOnlyList<THit>[] filled, int startIndex, IReadOnlyList<IReadOnlyList<THit>> aligned)
    {
        for (var j = 0; j < filled.Length - startIndex; j++)
        {
            filled[startIndex + j] = j < aligned.Count ? aligned[j] : Array.Empty<THit>();
        }
    }

    internal readonly record struct Result<THit>(
        IReadOnlyList<IReadOnlyList<THit>> ByDocument,
        int ScannedThrough,
        bool TruncatedByBudget);
}
