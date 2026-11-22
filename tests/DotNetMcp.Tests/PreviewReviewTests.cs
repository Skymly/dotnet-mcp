using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class PreviewReviewTests
{
    [Fact]
    public void large_sparse_file_shows_context_and_preserves_exact_line_endings()
    {
        var common = string.Concat(Enumerable.Range(1, 10_000).Select(i => $"line {i}\r\n"));
        var diff = Diff(common + "\r\nlast", common + "\nnew\n");

        Assert.Equal(2, diff.AddedLines);
        Assert.Equal(2, diff.RemovedLines);
        Assert.Equal("@@ -10000,3 +10000,3 @@\n line 10000\r\n-\r\n-last\n\\ No newline at end of file\n+\n+new\n", diff.Diff);
        Assert.Null(diff.NextOffset);
    }

    [Theory]
    [InlineData("", "one\n", "@@ -0,0 +1,1 @@\n+one\n")]
    [InlineData("one\n", "", "@@ -1,1 +0,0 @@\n-one\n")]
    [InlineData("", "", "")]
    [InlineData("a\r\n", "a\r\n", "")]
    public void empty_sides_and_no_change_have_exact_hunk_ranges(string oldText, string newText, string expected)
    {
        Assert.Equal(expected, Diff(oldText, newText).Diff);
        Assert.Empty(new PreviewReview([]).Page(0).Files);
    }

    [Fact]
    public void every_file_and_every_character_can_be_read_including_surrogate_pairs()
    {
        var longLine = string.Concat(Enumerable.Repeat("字😀", 6000));
        var input = Enumerable.Range(0, 17)
            .Select(i => new WorkspaceEditDocument($"file{i}", "old\n", $"{i}{longLine}\n"))
            .ToArray();
        var review = new PreviewReview(input);
        var group = review.Page(0);
        var seen = 0;
        while (true)
        {
            Assert.Equal(17, group.FileCount);
            foreach (var first in group.Files)
            {
                Assert.Equal(seen++, first.FileIndex);
                Assert.Equal(input[first.FileIndex].Path, first.Path);
                var diff = first.Diff;
                var chunk = first;
                while (chunk.NextOffset is int offset)
                {
                    var next = review.Read(0, first.FileIndex, offset);
                    Assert.Null(next.Error);
                    chunk = Assert.Single(next.Value!.Files);
                    Assert.False(char.IsLowSurrogate(chunk.Diff[0]));
                    Assert.InRange(chunk.Diff.Length, 1, 4096);
                    diff += chunk.Diff;
                }
                Assert.Equal($"@@ -1,1 +1,1 @@\n-old\n+{first.FileIndex}{longLine}\n", diff);
            }
            if (group.NextFileIndex is not int nextIndex)
            {
                break;
            }
            group = review.Read(nextIndex, null, 0).Value!;
        }
        Assert.Equal(input.Length, seen);
        Assert.Equal(PolicyErrorCodes.InvalidPreviewPage, review.Read(0, 17, 0).Error?.Error);
        Assert.Equal(PolicyErrorCodes.InvalidPreviewPage, review.Read(0, 1, int.MaxValue).Error?.Error);
        Assert.Equal(PolicyErrorCodes.InvalidPreviewPage, review.Read(1, 1, 0).Error?.Error);
    }

    private static PreviewFileDiff Diff(string before, string after) =>
        Assert.Single(new PreviewReview([new WorkspaceEditDocument("test", before, after)]).Page(0).Files);
}
