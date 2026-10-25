using DotNetMcp.Core;

namespace DotNetMcp.FSharp;

/// <summary>
/// symbol_source for F#: verbatim declaration text from the epoch
/// FSharpWorkspaceSnapshot — never the Roslyn side of the session. FCS
/// DeclarationLocation covers only the symbol identifier, so the span is
/// expanded over the text:
/// upward over contiguous /// doc lines and [&lt; attribute lines, downward by
/// the offside rule (the declaration ends before the next non-blank line
/// indented at or under the declaration head). Text stays verbatim — no
/// re-indent, no normalization.
/// </summary>
public sealed partial class FSharpSymbolQueryService
{
    public async Task<(SymbolSourceSuccess? Success, SymbolQueryError? Error)> GetSourceAsync(
        IWorkspaceSession session,
        string handle,
        int maxChars,
        CancellationToken cancellationToken = default)
    {
        var (item, error) = await TryResolveHandleAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        var slices = new List<SymbolSourceSlice>();
        foreach (var location in item!.Locations)
        {
            if (!string.Equals(location.DeclarationAvailability, DeclarationAvailability.InSource, StringComparison.Ordinal) ||
                location.Start is not { } start ||
                location.Length is not { } ||
                string.IsNullOrWhiteSpace(location.FilePath))
            {
                continue;
            }

            if (!TryGetSnapshot(session.FSharpSnapshot.Epoch, location.FilePath, out var path, out var text))
            {
                continue;
            }

            var (declStart, declEnd) = ExpandFSharpDeclaration(text, start);
            slices.Add(new SymbolSourceSlice(
                SymbolOrigin.Handwritten,
                Generator: null,
                path,
                declStart,
                declEnd,
                text));
        }

        if (slices.Count == 0)
        {
            return (null, new SourceUnavailableError(
                $"Symbol '{item!.SignatureQualifiedName}' has no source declarations in the F# snapshot.",
                "symbol_source only returns text for source-defined symbols; metadata-only symbols have no declaration text."));
        }

        var ordered = slices
            .OrderBy(s => s.FilePath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(s => s.Start)
            .ToArray();
        return (SymbolSourceText.Slice(ordered, maxChars, isTypeSymbol: item!.IsContainer), null);
    }

    /// <summary>
    /// Expand an identifier offset to the enclosing declaration: upward over
    /// contiguous /// and [&lt; lines, then downward while lines stay deeper
    /// indented than the declaration head (blank lines are absorbed but never
    /// trail). Returns [start, end) offsets into <paramref name="text"/> —
    /// start at the first character of the earliest attached line, end at the
    /// last content character of the last declaration line.
    /// </summary>
    private static (int Start, int End) ExpandFSharpDeclaration(string text, int idStart)
    {
        var lineStarts = LineStarts(text);
        var startLine = LineIndexOf(lineStarts, idStart);

        // Upward: attach contiguous /// doc-comment and [< attribute lines.
        var firstLine = startLine;
        while (firstLine > 0)
        {
            var previous = TrimmedLine(text, lineStarts[firstLine - 1]);
            if (previous.StartsWith("///", StringComparison.Ordinal) ||
                previous.StartsWith("[<", StringComparison.Ordinal))
            {
                firstLine--;
            }
            else
            {
                break;
            }
        }

        // Downward: the offside rule. The declaration head sits on the
        // identifier's line; deeper-indented content belongs to it.
        var declIndent = IndentOf(text, lineStarts[startLine]);
        var lastLine = startLine;
        for (var i = startLine + 1; i < lineStarts.Count; i++)
        {
            var raw = LineContent(text, lineStarts[i]);
            if (raw.IsWhiteSpace())
            {
                continue;
            }

            if (IndentOf(text, lineStarts[i]) > declIndent)
            {
                lastLine = i;
            }
            else
            {
                break;
            }
        }

        var start = lineStarts[firstLine] + IndentOf(text, lineStarts[firstLine]);
        var end = ContentEnd(text, lineStarts[lastLine]);
        return (start, end);
    }

    private static IReadOnlyList<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    private static int LineIndexOf(IReadOnlyList<int> lineStarts, int offset)
    {
        var line = 0;
        while (line + 1 < lineStarts.Count && lineStarts[line + 1] <= offset)
        {
            line++;
        }

        return line;
    }

    /// <summary>Leading whitespace length (spaces and tabs each count one).</summary>
    private static int IndentOf(string text, int lineStart)
    {
        var i = lineStart;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
        {
            i++;
        }

        return i - lineStart;
    }

    /// <summary>Line content without the line terminator.</summary>
    private static ReadOnlySpan<char> LineContent(string text, int lineStart) =>
        text.AsSpan(lineStart, ContentEnd(text, lineStart) - lineStart);

    /// <summary>Offset just past the last content char (before \r / \n / EOF).</summary>
    private static int ContentEnd(string text, int lineStart)
    {
        var i = lineStart;
        while (i < text.Length && text[i] is not '\r' and not '\n')
        {
            i++;
        }

        return i;
    }

    private static string TrimmedLine(string text, int lineStart) =>
        LineContent(text, lineStart).Trim().ToString();
}
