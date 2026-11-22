using System.Text;
using DiffPlex;

namespace DotNetMcp.Server;

public sealed record PreviewReviewPage(int FileCount, IReadOnlyList<PreviewFileDiff> Files, int? NextFileIndex);
public sealed record PreviewFileDiff(int FileIndex, string Path, int AddedLines, int RemovedLines, string Diff, int? NextOffset);

/// <summary>A read-only projection over the immutable slices held for apply.</summary>
internal sealed class PreviewReview(IReadOnlyList<WorkspaceEditDocument> documents)
{
    internal const string FormatDescription =
        "Use compact for file summaries and context diffs; continue with workspace_read_preview. Use full for complete oldText/newText. Defaults to full in the full profile, compact in lite.";
    private const int FilesPerPage = 8;
    private const int CharactersPerFile = 4096;
    private const int ContextLines = 1;

    // Compute only requested files, once per stored preview (including concurrent readers).
    private readonly Lazy<FileDiff>[] _files = documents
        .Select(doc => new Lazy<FileDiff>(() => Build(doc)))
        .ToArray();

    internal static bool TryGetFormat(string? format, out bool compact)
    {
        format ??= string.Equals(Environment.GetEnvironmentVariable("DOTNET_MCP_TOOL_PROFILE"), "lite",
            StringComparison.OrdinalIgnoreCase) ? "compact" : "full";
        compact = string.Equals(format, "compact", StringComparison.OrdinalIgnoreCase);
        return compact || string.Equals(format, "full", StringComparison.OrdinalIgnoreCase);
    }

    internal static PolicyErrorDto InvalidFormat() => new()
    {
        Error = PolicyErrorCodes.InvalidPreviewFormat,
        Message = "Unknown Workspace Edit preview format.",
        SuggestedAction = "Pass compact or full as format."
    };

    internal WorkspaceEditOutcome<PreviewReviewPage> Read(int startFileIndex, int? fileIndex, int offset)
    {
        if (startFileIndex < 0 || startFileIndex > 0 && startFileIndex >= _files.Length ||
            fileIndex is null && offset != 0 || fileIndex.HasValue && startFileIndex != 0)
        {
            return InvalidPage();
        }

        if (fileIndex is not int index)
        {
            return new(Page(startFileIndex), null);
        }

        if ((uint)index >= _files.Length)
        {
            return InvalidPage();
        }

        var diff = _files[index].Value.Text;
        if ((uint)offset > diff.Length || offset < diff.Length && char.IsLowSurrogate(diff[offset]))
        {
            return InvalidPage();
        }

        return new(new PreviewReviewPage(_files.Length, [Slice(index, offset)], null), null);
    }

    private static WorkspaceEditOutcome<PreviewReviewPage> InvalidPage() => new(null, new PolicyErrorDto
    {
        Error = PolicyErrorCodes.InvalidPreviewPage,
        Message = "The file index or diff offset is outside this preview or the paging arguments conflict.",
        SuggestedAction = "Pass startFileIndex=nextFileIndex for another group, or fileIndex and offset=nextOffset for a single file."
    });

    internal PreviewReviewPage Page(int start)
    {
        var count = Math.Min(FilesPerPage, _files.Length - start);
        var files = Enumerable.Range(start, count).Select(i => Slice(i, 0)).ToArray();
        var end = start + count;
        return new(_files.Length, files, end < _files.Length ? end : null);
    }

    private PreviewFileDiff Slice(int index, int offset)
    {
        var file = _files[index].Value;
        var length = Math.Min(CharactersPerFile, file.Text.Length - offset);
        var end = offset + length;
        if (end < file.Text.Length && char.IsLowSurrogate(file.Text[end]))
        {
            end--;
        }

        return new(index, documents[index].Path, file.Added, file.Removed,
            file.Text[offset..end], end < file.Text.Length ? end : null);
    }

    private sealed record FileDiff(string Text, int Added, int Removed);

    private static FileDiff Build(WorkspaceEditDocument document)
    {
        var diff = Differ.Instance.CreateDiffs(document.OldText, document.NewText, false, false, ExactLines.Instance);
        var blocks = diff.DiffBlocks;
        var output = new StringBuilder();
        int added = 0, removed = 0;
        for (var first = 0; first < blocks.Count;)
        {
            var last = first;
            while (last + 1 < blocks.Count && blocks[last + 1].DeleteStartA -
                   (blocks[last].DeleteStartA + blocks[last].DeleteCountA) <= 2 * ContextLines)
            {
                last++;
            }

            var begin = blocks[first];
            var finish = blocks[last];
            var before = Math.Min(ContextLines, begin.DeleteStartA);
            var after = Math.Min(ContextLines, diff.PiecesOld.Length - finish.DeleteStartA - finish.DeleteCountA);
            var oldStart = begin.DeleteStartA - before;
            var newStart = begin.InsertStartB - before;
            var oldEnd = finish.DeleteStartA + finish.DeleteCountA + after;
            var newEnd = finish.InsertStartB + finish.InsertCountB + after;
            output.Append(HunkHeader(oldStart, oldEnd, newStart, newEnd));

            var oldLine = oldStart;
            for (var i = first; i <= last; i++)
            {
                var block = blocks[i];
                AppendLines(output, ' ', diff.PiecesOld, oldLine, block.DeleteStartA);
                oldLine = block.DeleteStartA + block.DeleteCountA;
                AppendLines(output, '-', diff.PiecesOld, block.DeleteStartA, oldLine);
                AppendLines(output, '+', diff.PiecesNew, block.InsertStartB, block.InsertStartB + block.InsertCountB);
                added += block.InsertCountB;
                removed += block.DeleteCountA;
            }
            AppendLines(output, ' ', diff.PiecesOld, oldLine, oldEnd);
            first = last + 1;
        }
        return new(output.ToString(), added, removed);
    }

    private static string HunkHeader(int oldStart, int oldEnd, int newStart, int newEnd) =>
        FormattableString.Invariant($"@@ -{(oldStart == oldEnd ? oldStart : oldStart + 1)},{oldEnd - oldStart} +{(newStart == newEnd ? newStart : newStart + 1)},{newEnd - newStart} @@\n");

    private static void AppendLines(StringBuilder output, char prefix, string[] lines, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            var line = lines[i];
            output.Append(prefix).Append(line);
            if (!line.EndsWith('\r') && !line.EndsWith('\n'))
            {
                output.Append("\n\\ No newline at end of file\n");
            }
        }
    }

    // Keep line terminators and empty lines so a newline-only edit is fully visible.
    private sealed class ExactLines : IChunker
    {
        internal static readonly ExactLines Instance = new();
        public string[] Chunk(string text)
        {
            var lines = new List<string>();
            var start = 0;
            while (start < text.Length)
            {
                var at = text.AsSpan(start).IndexOfAny('\r', '\n');
                var end = at < 0 ? text.Length : start + at + 1;
                if (at >= 0 && text[end - 1] == '\r' && end < text.Length && text[end] == '\n')
                {
                    end++;
                }
                lines.Add(text[start..end]);
                start = end;
            }
            return lines.ToArray();
        }
    }
}
