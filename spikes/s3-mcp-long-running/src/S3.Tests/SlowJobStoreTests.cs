using Xunit;

namespace S3.Tests;

public class SlowJobStoreTests
{
    [Fact]
    public async Task Slow_open_returns_immediately_and_status_reaches_ready()
    {
        var store = new S3.Core.SlowJobStore();
        var job = store.Start(TimeSpan.FromMilliseconds(200), totalUnits: 4);

        var immediate = store.Snapshot(job.Id);
        Assert.Equal(job.Id, immediate.JobId);
        Assert.Contains(immediate.Phase, ["queued", "loading"]);
        Assert.Contains("slow_status", immediate.SuggestedAction);

        S3.Core.SlowJobStatusDto? ready = null;
        for (var i = 0; i < 50; i++)
        {
            await Task.Delay(50);
            ready = store.Snapshot(job.Id);
            if (ready.Phase == "ready")
            {
                break;
            }
        }

        Assert.NotNull(ready);
        Assert.Equal("ready", ready!.Phase);
        Assert.Equal(4, ready.CompletedUnits);
        Assert.Contains("Proceed", ready.SuggestedAction);
    }

    [Fact]
    public void Soft_budget_returns_partial_page_and_cursor()
    {
        var page = S3.Core.SoftBudgetPager.Page(
            cursor: null,
            pageSize: 50,
            totalItems: 100,
            budget: TimeSpan.FromMilliseconds(30),
            simulatedItemCost: TimeSpan.FromMilliseconds(10));

        Assert.True(page.Truncated);
        Assert.NotNull(page.NextCursor);
        Assert.InRange(page.Items.Count, 1, 3);
        Assert.Contains("nextCursor", page.Message);

        var page2 = S3.Core.SoftBudgetPager.Page(
            cursor: page.NextCursor,
            pageSize: 50,
            totalItems: 100,
            budget: TimeSpan.FromMilliseconds(30),
            simulatedItemCost: TimeSpan.FromMilliseconds(10));

        Assert.Equal(page.NextCursor, page2.Items[0].Replace("item-", ""));
    }
}
