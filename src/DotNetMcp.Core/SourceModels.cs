namespace DotNetMcp.Core;

/// <summary>
/// symbol_source output model. A slice is the verbatim declaration text plus the
/// origin attribution already computed by the language adapter; the shared
/// slicer applies the maxChars budget at line boundaries and derives the
/// unified position quad for the text actually returned.
/// </summary>
public sealed record SymbolSourceSlice(
    string Origin,
    GeneratorIdentity? Generator,
    string? FilePath,
    int Start,
    int End,
    string FullText);

public sealed record SymbolSourceDeclaration(
    string Origin,
    GeneratorIdentity? Generator,
    string? FilePath,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string Text,
    bool Truncated);

public sealed record SymbolSourceSuccess(
    IReadOnlyList<SymbolSourceDeclaration> Declarations,
    bool Truncated,
    string? Message);

public static class SymbolSourceText
{
    public const int DefaultMaxChars = 12_000;
    public const int MaxCharsCap = 60_000;

    /// <summary>
    /// Slice every declaration in order under a character budget. A declaration
    /// that fits is returned verbatim; one that does not is cut at the last
    /// line boundary inside the remaining budget (a CRLF pair is never split);
    /// everything after an exhausted budget is listed with empty text and
    /// <c>Truncated = true</c>. The position quad always describes the returned
    /// text — a truncated declaration's endLine/endColumn point at the cut.
    /// </summary>
    public static SymbolSourceSuccess Slice(
        IReadOnlyList<SymbolSourceSlice> slices,
        int maxChars,
        bool isTypeSymbol)
    {
        var declarations = new List<SymbolSourceDeclaration>(slices.Count);
        var remaining = maxChars;
        var budgetExhausted = false;
        var truncatedAny = false;
        foreach (var slice in slices)
        {
            var fullLength = Math.Max(0, slice.End - slice.Start);
            string text;
            var truncated = false;
            var end = slice.End;
            if (budgetExhausted)
            {
                text = string.Empty;
                end = slice.Start;
                truncated = true;
            }
            else if (fullLength <= remaining)
            {
                text = slice.FullText.Substring(slice.Start, fullLength);
                remaining -= fullLength;
            }
            else
            {
                end = LineBoundaryEnd(slice.FullText, slice.Start, slice.End, remaining);
                text = slice.FullText.Substring(slice.Start, end - slice.Start);
                truncated = true;
                budgetExhausted = true;
            }

            truncatedAny |= truncated;
            var (line, column) = LineColumn(slice.FullText, slice.Start);
            var (endLine, endColumn) = LineColumn(slice.FullText, end);
            declarations.Add(new SymbolSourceDeclaration(
                slice.Origin,
                slice.Generator,
                slice.FilePath,
                line,
                column,
                endLine,
                endColumn,
                text,
                truncated));
        }

        var message = truncatedAny
            ? isTypeSymbol
                ? "Declaration text exceeded the maxChars budget and was cut at a line boundary; call symbol_members on this handle, then call symbol_source on each member handle for complete text."
                : "Declaration text exceeded the maxChars budget and was cut at a line boundary; later declarations are listed with empty text."
            : null;
        return new SymbolSourceSuccess(declarations, truncatedAny, message);
    }

    /// <summary>
    /// Largest end position in [start, end] that lands on a line boundary and
    /// stays within <paramref name="maxLength"/> chars of start. A boundary is
    /// the position after a newline; a CRLF pair is a single boundary, so the
    /// cut never leaves a lone '\r'.
    /// </summary>
    private static int LineBoundaryEnd(string text, int start, int end, int maxLength)
    {
        var limit = Math.Min(end, start + Math.Max(0, maxLength));
        var cut = start;
        var i = start;
        while (i < limit)
        {
            if (text[i] == '\r' && i + 1 < end && text[i + 1] == '\n')
            {
                if (i + 2 <= limit)
                {
                    cut = i + 2;
                }

                i += 2;
                continue;
            }

            if (text[i] is '\r' or '\n')
            {
                cut = i + 1;
            }

            i++;
        }

        return cut;
    }

    /// <summary>1-based (line, column) of an offset, counting UTF-16 code units.</summary>
    private static (int Line, int Column) LineColumn(string text, int offset)
    {
        offset = Math.Min(offset, text.Length);
        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < offset; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return (line, offset - lineStart + 1);
    }
}
