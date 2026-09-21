using System.Text;
using System.Text.Json;
using DotNetMcp.Core;

namespace DotNetMcp.Tests;

public class SoftBudgetPageTests
{
    [Fact]
    public void scan_incomplete_with_items_that_fit_one_page_emits_cursor()
    {
        var items = new[] { "a", "b", "c" };

        var (page, error) = SoftBudgetPage.Page(
            items,
            epoch: 7,
            budgetHit: true,
            cursor: null,
            pageLimit: 50,
            tool: "symbol_members",
            queryId: "h1",
            emptyMessage: "No members.",
            completeMessage: "Page complete.",
            scanIncomplete: true);

        Assert.Null(error);
        Assert.NotNull(page);
        Assert.Equal(items, page!.Items);
        Assert.True(page.Truncated);
        Assert.False(string.IsNullOrWhiteSpace(page.NextCursor));
        Assert.DoesNotContain("Page complete.", page.Message, StringComparison.Ordinal);
        Assert.Contains("Soft budget", page.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(MemberPageCursor.TryDecode(
            page.NextCursor,
            out var epoch,
            out var offset,
            out var tool,
            out var queryId,
            out var cursorError));
        Assert.Null(cursorError);
        Assert.Equal(7, epoch);
        Assert.Equal(3, offset);
        Assert.Equal("symbol_members", tool);
        Assert.Equal("h1", queryId);
    }

    [Fact]
    public void budget_hit_find_refs_with_more_items_keeps_find_refs_cursor_payload()
    {
        var items = new[] { "r1", "r2", "r3" };

        var (page, error) = SoftBudgetPage.PageFindRefs(
            items,
            epoch: 3,
            entireSolution: true,
            budgetHit: true,
            cursor: null,
            pageLimit: 2,
            tool: "symbol_find_references",
            queryId: "href",
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
            out var tool,
            out var queryId,
            out var cursorError));
        Assert.Null(cursorError);
        Assert.Equal(3, epoch);
        Assert.True(entire);
        Assert.Equal(2, docIndex);
        Assert.Equal(0, locOffset);
        Assert.Equal("symbol_find_references", tool);
        Assert.Equal("href", queryId);
        Assert.DoesNotContain("Page complete.", page.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void member_cursor_wrong_tool_or_handle_is_stale()
    {
        var (page, _) = SoftBudgetPage.Page(
            new[] { "a", "b", "c" },
            epoch: 1,
            budgetHit: false,
            cursor: null,
            pageLimit: 1,
            tool: "symbol_members",
            queryId: "ha",
            emptyMessage: "none",
            completeMessage: "done");
        Assert.False(string.IsNullOrWhiteSpace(page!.NextCursor));

        var (_, wrongTool) = SoftBudgetPage.Page(
            new[] { "a", "b", "c" },
            epoch: 1,
            budgetHit: false,
            cursor: page.NextCursor,
            pageLimit: 1,
            tool: "symbol_find_implementations",
            queryId: "ha",
            emptyMessage: "none",
            completeMessage: "done");
        Assert.IsType<StaleCursorError>(wrongTool);
        Assert.Contains("different tool or symbol", wrongTool!.Message, StringComparison.OrdinalIgnoreCase);

        var (_, wrongHandle) = SoftBudgetPage.Page(
            new[] { "a", "b", "c" },
            epoch: 1,
            budgetHit: false,
            cursor: page.NextCursor,
            pageLimit: 1,
            tool: "symbol_members",
            queryId: "hb",
            emptyMessage: "none",
            completeMessage: "done");
        Assert.IsType<StaleCursorError>(wrongHandle);
    }

    [Fact]
    public void find_refs_cursor_wrong_handle_is_stale()
    {
        var (page, _) = SoftBudgetPage.PageFindRefs(
            new[] { "r1", "r2", "r3" },
            epoch: 1,
            entireSolution: false,
            budgetHit: false,
            cursor: null,
            pageLimit: 1,
            tool: "symbol_find_references",
            queryId: "ha",
            emptyMessage: "none",
            completeMessage: "done");

        var (_, error) = SoftBudgetPage.PageFindRefs(
            new[] { "r1", "r2", "r3" },
            epoch: 1,
            entireSolution: false,
            budgetHit: false,
            cursor: page!.NextCursor,
            pageLimit: 1,
            tool: "symbol_find_references",
            queryId: "hb",
            emptyMessage: "none",
            completeMessage: "done");
        Assert.IsType<StaleCursorError>(error);
    }

    [Fact]
    public void find_refs_v1_cursor_is_rejected_after_dependent_scope_flip()
    {
        var json = JsonSerializer.Serialize(new
        {
            V = "v1",
            Epoch = 1L,
            EntireSolution = false,
            DocIndex = 0,
            LocOffset = 0,
            IssuedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
        var cursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        Assert.False(FindRefsPageCursor.TryDecode(
            cursor, out _, out _, out _, out _, out _, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
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
            out var cursorTool,
            out var cursorError));
        Assert.Null(cursorError);
        Assert.Equal(4, epoch);
        Assert.Equal("Gen", assembly);
        Assert.Equal("G.T", type);
        Assert.Equal(2, offset);
        Assert.Equal("project_list_generated_sources", cursorTool);
        Assert.DoesNotContain("Page complete.", page.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void generated_cursor_wrong_identity_is_stale()
    {
        var cursor = GeneratedSourcesPageCursor.Encode(4, "Gen", "G.T", 0, "project_list_generated_sources");

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
    public void generated_cursor_from_other_tool_is_stale()
    {
        var cursor = GeneratedSourcesPageCursor.Encode(
            4, "Gen", "G.T", 0, "project_list_generated_sources");

        var (_, error) = SoftBudgetPage.PageGenerated(
            new[] { "s1", "s2" },
            epoch: 4,
            assemblyName: "Gen",
            typeFullName: "G.T",
            cursor: cursor,
            pageLimit: 10,
            tool: "project_list_generator_diagnostics",
            emptyMessage: "none",
            completeMessage: "done");

        Assert.IsType<StaleCursorError>(error);
        Assert.Contains("different tool", error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void generated_cursor_same_tool_continues()
    {
        var (first, firstError) = SoftBudgetPage.PageGenerated(
            new[] { "s1", "s2", "s3" },
            epoch: 4,
            assemblyName: "Gen",
            typeFullName: "G.T",
            cursor: null,
            pageLimit: 2,
            tool: "project_list_generated_sources",
            emptyMessage: "none",
            completeMessage: "done");

        Assert.Null(firstError);
        Assert.NotNull(first);
        Assert.True(first!.Truncated);

        var (second, secondError) = SoftBudgetPage.PageGenerated(
            new[] { "s1", "s2", "s3" },
            epoch: 4,
            assemblyName: "Gen",
            typeFullName: "G.T",
            cursor: first.NextCursor,
            pageLimit: 2,
            tool: "project_list_generated_sources",
            emptyMessage: "none",
            completeMessage: "done");

        Assert.Null(secondError);
        Assert.Equal(new[] { "s3" }, second!.Items);
        Assert.False(second.Truncated);
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