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

    [Fact]
    public void generated_cursor_keeps_generator_identity_payload()
    {
        var items = new[] { "s1", "s2", "s3" };

        var (page, error) = SoftBudgetPage.PageGenerated(
            items,
            epoch: 4,
            assemblyName: "Gen",
            typeFullName: "G.T",
            cursor: null,
            pageLimit: 2,
            tool: "project_list_generated_sources",
            emptyMessage: "none",
            completeMessage: "Page complete.");

        Assert.Null(error);
        Assert.NotNull(page);
        Assert.True(page!.Truncated);
        Assert.Equal(new[] { "s1", "s2" }, page.Items);
        Assert.True(GeneratedSourcesPageCursor.TryDecode(
            page.NextCursor,
            out var epoch,
            out var assembly,
            out var type,
            out var offset,
            out var cursorError));
        Assert.Null(cursorError);
        Assert.Equal(4, epoch);
        Assert.Equal("Gen", assembly);
        Assert.Equal("G.T", type);
        Assert.Equal(2, offset);
        Assert.DoesNotContain("Page complete.", page.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void generated_cursor_wrong_identity_is_stale()
    {
        var cursor = GeneratedSourcesPageCursor.Encode(4, "Gen", "G.T", 0);

        var (_, error) = SoftBudgetPage.PageGenerated(
            new[] { "s1" },
            epoch: 4,
            assemblyName: "Other",
            typeFullName: "G.T",
            cursor: cursor,
            pageLimit: 10,
            tool: "project_list_generated_sources",
            emptyMessage: "none",
            completeMessage: "done");

        Assert.IsType<StaleCursorError>(error);
        Assert.Contains("identity", error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void list_query_modules_do_not_hand_decode_cursors()
    {
        var coreDir = FindCoreDir();
        foreach (var name in new[] { "DynamicInvocationQueryService.cs", "GeneratorQueryService.cs" })
        {
            var text = File.ReadAllText(Path.Combine(coreDir, name));
            Assert.DoesNotContain("MemberPageCursor.TryDecode", text, StringComparison.Ordinal);
            Assert.DoesNotContain("GeneratedSourcesPageCursor.TryDecode", text, StringComparison.Ordinal);
            Assert.Contains("SoftBudgetPage.", text, StringComparison.Ordinal);
        }
    }

    private static string FindCoreDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "DotNetMcp.Core");
            if (File.Exists(Path.Combine(candidate, "SoftBudgetPage.cs")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate src/DotNetMcp.Core from the test assembly.");
    }
}