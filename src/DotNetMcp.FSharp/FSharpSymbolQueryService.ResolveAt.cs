using DotNetMcp.Core;
using FSharp.Compiler.CodeAnalysis;
using FSharp.Compiler.Symbols;
using FcsRange = global::FSharp.Compiler.Text.Range;

namespace DotNetMcp.FSharp;

public sealed partial class FSharpSymbolQueryService
{
    /// <summary>
    /// Position entry (ADR-0001 Amendment 7). Documents are enumerated from
    /// <see cref="IWorkspaceSession.FSharpSnapshot"/> only — the F# adapter never
    /// reads the Roslyn Solution (guard test enforces this). The resolved
    /// FSharpSymbol is matched against the same catalog that
    /// symbol_resolve/symbol_summary use, so the emitted handle re-resolves.
    /// </summary>
    public async Task<(SymbolResolveAtSuccess? Success, SymbolQueryError? Error)> ResolveAtPositionAsync(
        IWorkspaceSession session,
        string path,
        int line,
        int column,
        string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        using var epochHold = EnterRequest(session.FSharpSnapshot.Epoch);

        var matches = new List<(FSharpProjectSnapshot Project, FSharpDocumentSnapshot Document)>();
        foreach (var fsProject in session.FSharpSnapshot.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(projectId) &&
                !string.Equals(fsProject.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var fsDocument in fsProject.Documents)
            {
                if (SameDocumentPath(fsDocument.Path, path))
                {
                    matches.Add((fsProject, fsDocument));
                }
            }
        }

        if (matches.Count == 0)
        {
            return (null, new DocumentNotInWorkspaceError(
                $"No F# document '{path}' is part of the ready workspace.",
                "Feed filePath from project_diagnostics / symbol_find_references / symbol_goto_definition verbatim, or check workspace_list_projects for the right projectId."));
        }

        var (project, document) = matches[0];
        var text = document.Text;
        var lineCount = LineCountOf(text);
        if (line < 1 || line > lineCount || column < 1)
        {
            return (null, new InvalidPositionError(
                $"Position ({line},{column}) is outside the document (lines 1..{lineCount}, columns start at 1).",
                "Pass the 1-based line/column emitted by another tool for this document."));
        }

        // Unified 1-based column -> FCS 0-based column, clamped to the line end.
        var fcsColumn = Math.Min(column - 1, LineLengthOf(text, line));

        var (items, check, _) = await CheckProjectAsync(
                session.FSharpSnapshot, project, session.FSharpSnapshot.Epoch, cancellationToken)
            .ConfigureAwait(false);
        if (check is null)
        {
            return (null, new CompilationUnavailableError(
                $"F# project '{project.Name}' did not produce a check result.",
                "Fix project load errors first (project_diagnostics), then retry symbol_resolve_at."));
        }

        var uses = check.GetAllUsesOfAllSymbols(null)
            .Where(use => SameDocumentPath(use.FileName, document.Path))
            .ToList();

        var hit = uses
            .Where(use => RangeContainsPosition(use.Range, line, fcsColumn))
            .OrderBy(use => RangeSize(use.Range))
            .FirstOrDefault() ?? uses
            .Where(use => use.Range.StartLine == line && use.Range.StartColumn <= fcsColumn)
            .OrderByDescending(use => use.Range.EndColumn)
            .FirstOrDefault();

        if (hit is null)
        {
            return (null, new NoSymbolAtPositionError(
                $"No F# symbol binds at position ({line},{column}) in '{document.Path}'.",
                "Point at an identifier (a declaration or a reference) emitted by another tool."));
        }

        var catalog = FlattenCatalog(items).ToList();
        var item = MatchCatalog(catalog, hit.Symbol);
        if (item is null)
        {
            var enclosing = EnclosingCatalogMember(catalog, uses, document.Path, line, fcsColumn);
            var display = hit.Symbol.DisplayName;
            return (null, new SymbolNotAddressableError(
                $"F# symbol '{display}' ({hit.Symbol.GetType().Name}) has no stable SymbolHandle — only modules/types and their members are addressable." +
                (enclosing is null
                    ? string.Empty
                    : $" It is declared inside member '{enclosing.SignatureQualifiedName}'."),
                enclosing is null
                    ? "Resolve the enclosing module/type or member with symbol_resolve (by name) to obtain a SymbolHandle."
                    : $"Call symbol_resolve for '{enclosing.SignatureQualifiedName}' to obtain a SymbolHandle for the containing member."));
        }

        var success = ToSuccess(item);
        var (start, length) = ToSpan(text, hit.Range);
        var matched = new MatchedIdentifier(
            hit.Range.StartLine,
            hit.Range.StartColumn + 1,
            hit.Range.EndLine,
            hit.Range.EndColumn + 1,
            text.Substring(start, length));
        var resolvedFrom = hit.IsFromDefinition
            ? SymbolResolveFrom.Declaration
            : SymbolResolveFrom.Reference;
        return (new SymbolResolveAtSuccess(
            success.Handle,
            success.Summary,
            matched,
            resolvedFrom,
            CandidateReason: null), null);
    }

    private static bool RangeContainsPosition(FcsRange range, int line, int column0) =>
        (range.StartLine < line || (range.StartLine == line && range.StartColumn <= column0)) &&
        (range.EndLine > line || (range.EndLine == line && range.EndColumn >= column0));

    /// <summary>
    /// Nearest preceding definition that maps to a non-container catalog item —
    /// names the enclosing member for SymbolNotAddressable messages.
    /// </summary>
    private static FSharpCatalogItem? EnclosingCatalogMember(
        IReadOnlyList<FSharpCatalogItem> catalog,
        IReadOnlyList<FSharpSymbolUse> uses,
        string path,
        int line,
        int column0)
    {
        return uses
            .Where(use => use.IsFromDefinition && SameDocumentPath(use.FileName, path))
            .Where(use => use.Range.StartLine < line ||
                          (use.Range.StartLine == line && use.Range.StartColumn <= column0))
            .OrderByDescending(use => use.Range.StartLine)
            .ThenByDescending(use => use.Range.StartColumn)
            .Select(use => MatchCatalog(catalog, use.Symbol))
            .FirstOrDefault(item => item is { IsContainer: false });
    }

    private static int LineCountOf(string text)
    {
        var count = 1;
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static int LineLengthOf(string text, int line1Based)
    {
        var line = 1;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == '\n')
            {
                if (line == line1Based)
                {
                    var end = i;
                    if (end > start && text[end - 1] == '\r')
                    {
                        end--;
                    }

                    return end - start;
                }

                line++;
                start = i + 1;
            }
        }

        return 0;
    }
}
