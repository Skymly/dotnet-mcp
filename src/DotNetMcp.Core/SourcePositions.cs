using Microsoft.CodeAnalysis;

namespace DotNetMcp.Core;

/// <summary>
/// Unified position model: 1-based lines; 1-based columns counted in UTF-16 code
/// units (a tab is one column), matching csc / MSBuild file(line,col). The end
/// column points just past the last character (exclusive). Locations without a
/// source position leave all four fields null.
/// </summary>
internal static class SourcePositions
{
    public static (int Line, int Column, int EndLine, int EndColumn) FromLineSpan(
        FileLinePositionSpan span) =>
        (span.StartLinePosition.Line + 1,
         span.StartLinePosition.Character + 1,
         span.EndLinePosition.Line + 1,
         span.EndLinePosition.Character + 1);
}
