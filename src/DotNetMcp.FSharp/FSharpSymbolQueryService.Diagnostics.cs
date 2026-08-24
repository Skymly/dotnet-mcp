using System.Diagnostics;
using DotNetMcp.Core;

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
        var project = session.FSharpSnapshot.FindProject(projectId);
        if (project is null)
        {
            return (null, new ProjectNotFoundError(
                $"No F# project with projectId '{projectId}' is in the ready workspace.",
                "Call workspace_list_projects for valid projectId values, then retry project_diagnostics."));
        }

        var epoch = session.Epoch;
        var pageLimit = limit is null or < 1
            ? LanguageAdapters.DefaultMemberPageLimit
            : Math.Min(limit.Value, LanguageAdapters.MaxMemberPageLimit);
        if (!SoftBudgetPage.TryReadOffset(cursor, epoch, "project_diagnostics", out _, out var cursorError))
        {
            return (null, cursorError);
        }

        var budget = softBudget ?? _softBudgets.SingleProjectCompile;
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

        return SoftBudgetPage.Page(
            items,
            epoch,
            budgetHit: clock.Elapsed >= budget,
            cursor,
            pageLimit,
            "project_diagnostics",
            "Project has no error or warning diagnostics.",
            "Diagnostics page complete.",
            "the diagnostics list");
    }
}
