namespace DotNetMcp.Core;

/// <summary>
/// Soft budget paging ceremony (ADR-0003). Cursor payloads stay on
/// <see cref="MemberPageCursor"/> / <see cref="FindRefsPageCursor"/>.
/// </summary>
public static class SoftBudgetPage
{
    public static (PagedResult<T>? Success, SymbolQueryError? Error) Page<T>(
        IReadOnlyList<T> items,
        long epoch,
        bool budgetHit,
        string? cursor,
        int pageLimit,
        string tool,
        string emptyMessage,
        string completeMessage,
        string pastEndNoun = "the result list")
    {
        if (!TryReadOffset(cursor, epoch, tool, out var offset, out var error))
        {
            return (null, error);
        }

        if (offset > items.Count)
        {
            return (null, PastEnd(tool, pastEndNoun));
        }

        pageLimit = Math.Max(1, pageLimit);
        var slice = items.Skip(offset).Take(pageLimit).ToList();
        var next = offset + slice.Count;
        return (Finish(
            slice,
            moreItems: next < items.Count,
            budgetHit,
            () => MemberPageCursor.Encode(epoch, next),
            tool,
            items.Count == 0 ? emptyMessage : completeMessage), null);
    }

    public static (PagedResult<T>? Success, SymbolQueryError? Error) PageFindRefs<T>(
        IReadOnlyList<T> items,
        long epoch,
        bool entireSolution,
        bool budgetHit,
        string? cursor,
        int pageLimit,
        string tool,
        string emptyMessage,
        string completeMessage)
    {
        if (!TryReadFindRefs(cursor, epoch, entireSolution, tool, out var docIndex, out var locOffset, out var error))
        {
            return (null, error);
        }

        var offset = docIndex + locOffset;
        if (offset > items.Count)
        {
            return (null, PastEnd(tool, "the result list"));
        }

        pageLimit = Math.Max(1, pageLimit);
        var slice = items.Skip(offset).Take(pageLimit).ToList();
        var next = offset + slice.Count;
        return (Finish(
            slice,
            moreItems: next < items.Count,
            budgetHit,
            () => FindRefsPageCursor.Encode(epoch, entireSolution, next, 0),
            tool,
            items.Count == 0 ? emptyMessage : completeMessage), null);
    }

    public static (PagedResult<T>? Success, SymbolQueryError? Error) PageGenerated<T>(
        IReadOnlyList<T> items,
        long epoch,
        string assemblyName,
        string typeFullName,
        string? cursor,
        int pageLimit,
        string tool,
        string emptyMessage,
        string completeMessage,
        string pastEndNoun = "the result list")
    {
        if (!TryReadGenerated(cursor, epoch, assemblyName, typeFullName, tool, out var offset, out var error))
        {
            return (null, error);
        }

        if (offset > items.Count)
        {
            return (null, PastEnd(tool, pastEndNoun));
        }

        pageLimit = Math.Max(1, pageLimit);
        var slice = items.Skip(offset).Take(pageLimit).ToList();
        var next = offset + slice.Count;
        return (Finish(
            slice,
            moreItems: next < items.Count,
            budgetHit: false,
            () => GeneratedSourcesPageCursor.Encode(epoch, assemblyName, typeFullName, next),
            tool,
            items.Count == 0 ? emptyMessage : completeMessage), null);
    }

    public static bool TryReadOffset(
        string? cursor,
        long epoch,
        string tool,
        out int offset,
        out SymbolQueryError? error)
    {
        offset = 0;
        error = null;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        if (!MemberPageCursor.TryDecode(cursor, out var cursorEpoch, out offset, out var cursorError))
        {
            error = new StaleCursorError(
                cursorError ?? "Cursor is invalid.",
                $"Call {tool} again without a cursor to start a fresh page.");
            offset = 0;
            return false;
        }

        if (cursorEpoch != epoch)
        {
            error = new StaleCursorError(
                $"Cursor epoch {cursorEpoch} does not match workspace epoch {epoch}.",
                $"Call {tool} again without a cursor; do not retry with the stale cursor.");
            offset = 0;
            return false;
        }

        return true;
    }

    public static bool TryReadFindRefs(
        string? cursor,
        long epoch,
        bool entireSolution,
        string tool,
        out int docIndex,
        out int locOffset,
        out SymbolQueryError? error,
        string? scopeMismatchMessage = null)
    {
        docIndex = 0;
        locOffset = 0;
        error = null;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        if (!FindRefsPageCursor.TryDecode(
                cursor,
                out var cursorEpoch,
                out var cursorEntire,
                out docIndex,
                out locOffset,
                out var cursorError))
        {
            error = new StaleCursorError(
                cursorError ?? "Cursor is invalid.",
                $"Call {tool} again without a cursor to start a fresh page.");
            docIndex = 0;
            locOffset = 0;
            return false;
        }

        if (cursorEpoch != epoch || cursorEntire != entireSolution)
        {
            error = new StaleCursorError(
                cursorEpoch != epoch
                    ? $"Cursor epoch {cursorEpoch} does not match workspace epoch {epoch}."
                    : scopeMismatchMessage ?? "Cursor does not match the current workspace epoch or scope.",
                $"Call {tool} again without a cursor; do not retry with the stale cursor.");
            docIndex = 0;
            locOffset = 0;
            return false;
        }

        return true;
    }

    public static bool TryReadGenerated(
        string? cursor,
        long epoch,
        string assemblyName,
        string typeFullName,
        string tool,
        out int offset,
        out SymbolQueryError? error)
    {
        offset = 0;
        error = null;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        if (!GeneratedSourcesPageCursor.TryDecode(
                cursor,
                out var cursorEpoch,
                out var cursorAssembly,
                out var cursorType,
                out offset,
                out var cursorError))
        {
            error = new StaleCursorError(
                cursorError ?? "Cursor is invalid.",
                $"Call {tool} again without a cursor to start a fresh page.");
            offset = 0;
            return false;
        }

        if (cursorEpoch != epoch)
        {
            error = new StaleCursorError(
                $"Cursor epoch {cursorEpoch} does not match workspace epoch {epoch}.",
                $"Call {tool} again without a cursor; do not retry with the stale cursor.");
            offset = 0;
            return false;
        }

        if (!string.Equals(cursorAssembly, assemblyName, StringComparison.Ordinal) ||
            !string.Equals(cursorType, typeFullName, StringComparison.Ordinal))
        {
            error = new StaleCursorError(
                "Cursor generator identity does not match assemblyName/typeFullName.",
                "Pass the same assemblyName and typeFullName used when the cursor was issued.");
            offset = 0;
            return false;
        }

        return true;
    }

    public static PagedResult<T> Finish<T>(
        IReadOnlyList<T> slice,
        bool moreItems,
        bool budgetHit,
        Func<string> encodeNextCursor,
        string tool,
        string doneMessage)
    {
        var truncated = moreItems || budgetHit;
        return new PagedResult<T>(
            slice,
            truncated,
            truncated ? encodeNextCursor() : null,
            truncated
                ? budgetHit
                    ? $"Soft budget reached after {slice.Count} item(s). Pass nextCursor to {tool} to continue; do not retry from scratch."
                    : $"Results truncated; pass nextCursor to {tool} to continue (do not restart from the first page)."
                : doneMessage);
    }

    public static StaleCursorError PastEnd(string tool, string noun) =>
        new(
            $"Cursor offset is past the end of {noun}.",
            $"Call {tool} again without a cursor to start a fresh page.");
}
