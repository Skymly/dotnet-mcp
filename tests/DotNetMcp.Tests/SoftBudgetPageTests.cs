using DotNetMcp.Core;

namespace DotNetMcp.Tests;

public class SoftBudgetPageTests
{
    [Fact]
    public void budget_hit_with_items_that_fit_one_page_is_truncated_not_complete()
    {
        var items = new[] { "a", "b", "c" };

        var (page, error) = SoftBudgetPage.Page(
            items,
            epoch: 7,
            budgetHit: true,
            cursor: null,
            pageLimit: 50,
            tool: "symbol_find_references",
            emptyMessage: "No references were found.",
            completeMessage: "Page complete.");

        Assert.Null(error);
        Assert.NotNull(page);
        Assert.Equal(items, page!.Items);
        Assert.True(page.Truncated);
        Assert.False(string.IsNullOrWhiteSpace(page.NextCursor));
        Assert.Contains("Soft budget", page.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nextCursor", page.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Page complete.", page.Message, StringComparison.Ordinal);

        Assert.True(MemberPageCursor.TryDecode(page.NextCursor, out var epoch, out var offset, out var cursorError));
        Assert.Null(cursorError);
        Assert.Equal(7, epoch);
        Assert.Equal(items.Length, offset);
    }

    [Fact]
    public void budget_hit_find_refs_keeps_find_refs_cursor_payload()
    {
        var items = new[] { "r1", "r2" };

        var (page, error) = SoftBudgetPage.PageFindRefs(
            items,
            epoch: 3,
            entireSolution: true,
            budgetHit: true,
            cursor: null,
            pageLimit: 10,
            tool: "symbol_find_references",
            emptyMessage: "No references were found.",
            completeMessage: "Page complete.");

        Assert.Null(error);
        Assert.NotNull(page);
        Assert.True(page!.Truncated);
        Assert.True(FindRefsPageCursor.TryDecode(
            page.NextCursor,
            out var epoch,
            out var entire,
            out var docIndex,
            out var locOffset,
            out var cursorError));
        Assert.Null(cursorError);
        Assert.Equal(3, epoch);
        Assert.True(entire);
        Assert.Equal(items.Length, docIndex);
        Assert.Equal(0, locOffset);
        Assert.DoesNotContain("Page complete.", page.Message, StringComparison.Ordinal);
    }
}
