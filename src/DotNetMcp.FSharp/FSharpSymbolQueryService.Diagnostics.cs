using System.Diagnostics;
using DotNetMcp.Core;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.FSharp;

public sealed partial class FSharpSymbolQueryService
{
    public async Task<(PagedResult<DiagnosticItem>? Success, SymbolQueryError? Error)> GetProjectDiagnosticsAsync(
        IWorkspaceSession session,
        string projectId,
        int? limit = null,
        string? cursor = null,
        TimeSpan? softBudget = null,
        CancellationToken cancellationToken = default)
    {
        var project = session.Solution.Projects.FirstOrDefault(p =>
            p.Language == LanguageNames.FSharp &&
            string.Equals(p.Id.Id.ToString("D"), projectId, StringComparison.OrdinalIgnoreCase));
        if (project is null)
        {
            return (null, new ProjectNotFoundError(
                $"No F# project with projectId '{projectId}' is in the ready workspace.",
                "Call workspace_list_projects for valid projectId values, then retry project_diagnostics."));
        }

        var epoch = session.Epoch;
        var pageLimit = limit is null or < 1
            ? SymbolQueryService.DefaultMemberPageLimit
            : Math.Min(limit.Value, SymbolQueryService.MaxMemberPageLimit);
        var offset = 0;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!MemberPageCursor.TryDecode(cursor, out var cursorEpoch, out offset, out var cursorError))
            {
                return (null, new StaleCursorError(
                    cursorError ?? "Cursor is invalid.",
                    "Call project_diagnostics again without a cursor to start a fresh page."));
            }

            if (cursorEpoch != epoch)
            {
                return (null, new StaleCursorError(
                    $"Cursor epoch {cursorEpoch} does not match workspace epoch {epoch}.",
                    "Call project_diagnostics again without a cursor; do not retry with the stale cursor."));
            }
        }

        var budget = softBudget ?? SoftBudgetOptions.Default.SingleProjectCompile;
        var clock = Stopwatch.StartNew();
        var (_, check, _) = await CheckProjectAsync(project, cancellationToken).ConfigureAwait(false);
        if (check is null)
        {
            return (null, new CompilationUnavailableError(
                $"FCS check is unavailable for project '{project.Name}'.",
                "Retry project_diagnostics; if it keeps failing, call workspace_list_projects and confirm the projectId."));
        }

        var items = check.Diagnostics
            .Where(d => d.Severity.ToString() is "Error" or "Warning")
            .Select(d => new DiagnosticItem(
                Id: d.ErrorNumber > 0 ? $"FS{d.ErrorNumber:D4}" : (d.Subcategory ?? "FS"),
                Severity: d.Severity.ToString(),
                Message: d.Message,
                FilePath: string.IsNullOrWhiteSpace(d.FileName) ? null : d.FileName,
                StartLine: d.StartLine,
                StartCharacter: d.StartColumn,
                EndLine: d.EndLine,
                EndCharacter: d.EndColumn,
                ProjectId: projectId))
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .ThenBy(d => d.FilePath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(d => d.StartLine ?? -1)
            .ThenBy(d => d.StartCharacter ?? -1)
            .ThenBy(d => d.Message, StringComparer.Ordinal)
            .ToList();

        if (clock.Elapsed >= budget && items.Count == 0)
        {
            return (new PagedResult<DiagnosticItem>(
                [],
                Truncated: true,
                NextCursor: MemberPageCursor.Encode(epoch, offset),
                Message: "Soft budget reached after 0 item(s). Pass nextCursor to continue; do not retry from scratch."), null);
        }

        if (offset > items.Count)
        {
            return (null, new StaleCursorError(
                "Cursor offset is past the end of the diagnostics list.",
                "Call project_diagnostics again without a cursor to start a fresh page."));
        }

        var slice = items.Skip(offset).Take(pageLimit).ToList();
        var next = offset + slice.Count;
        var truncated = next < items.Count;
        return (new PagedResult<DiagnosticItem>(
            slice,
            truncated,
            truncated ? MemberPageCursor.Encode(epoch, next) : null,
            truncated
                ? "Results truncated; pass nextCursor to project_diagnostics to continue (do not restart from the first page)."
                : items.Count == 0
                    ? "Project has no error or warning diagnostics."
                    : "Diagnostics page complete."), null);
    }
}
