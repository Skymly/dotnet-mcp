using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;

namespace DotNetMcp.Core;

public sealed partial class RoslynLanguageAdapter
{
    public async Task<(PagedResult<DiagnosticItem>? Success, SymbolQueryError? Error)> GetProjectDiagnosticsAsync(
        IWorkspaceSession session,
        string projectId,
        int? limit = null,
        string? cursor = null,
        TimeSpan? softBudget = null,
        CancellationToken cancellationToken = default)
    {
        var project = session.Solution.Projects
            .Where(p => OwnsProject(p))
            .FirstOrDefault(p =>
                string.Equals(p.Id.Id.ToString("D"), projectId, StringComparison.OrdinalIgnoreCase));

        if (project is null)
        {
            return (null, new ProjectNotFoundError(
                $"No project with projectId '{projectId}' is in the ready workspace.",
                "Call workspace_list_projects for valid projectId values, then retry project_diagnostics."));
        }

        var epoch = session.Epoch;
        var pageLimit = ClampLimit(limit);
        if (!SoftBudgetPage.TryReadOffset(
                cursor,
                epoch,
                "project_diagnostics",
                out var offset,
                out var cursorError))
        {
            return (null, cursorError);
        }

        var budget = softBudget ?? _softBudgets.SingleProjectCompile;
        Compilation? compilation;
        using (var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            if (budget <= TimeSpan.Zero)
            {
                budgetCts.Cancel();
            }
            else
            {
                budgetCts.CancelAfter(budget);
            }

            try
            {
                compilation = await session.GetCompilationAsync(project.Id, budgetCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (SoftBudgetPage.Finish(
                    Array.Empty<DiagnosticItem>(),
                    moreItems: false,
                    budgetHit: true,
                    () => MemberPageCursor.Encode(epoch, offset),
                    "project_diagnostics",
                    "Project has no error or warning diagnostics."), null);
            }
            catch (InvalidOperationException ex)
            {
                return (null, new CompilationUnavailableError(
                    ex.Message,
                    "Retry project_diagnostics; if it keeps failing, call workspace_list_projects and confirm the projectId."));
            }
        }

        if (compilation is null)
        {
            return (null, new CompilationUnavailableError(
                $"Compilation is unavailable for project '{project.Name}'.",
                "Retry project_diagnostics; if it keeps failing, call workspace_list_projects and confirm the projectId."));
        }

        var projectIdString = project.Id.Id.ToString("D");
        var diagnostics = compilation.GetDiagnostics(cancellationToken)
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(d => ToDiagnosticItem(d, projectIdString))
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .ThenBy(d => d.FilePath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(d => d.StartLine ?? -1)
            .ThenBy(d => d.StartCharacter ?? -1)
            .ThenBy(d => d.Message, StringComparer.Ordinal)
            .ToList();

        return SoftBudgetPage.Page(
            diagnostics,
            epoch,
            budgetHit: false,
            cursor,
            pageLimit,
            "project_diagnostics",
            "Project has no error or warning diagnostics.",
            "Diagnostics page complete.",
            "the diagnostics list");
    }

    private static DiagnosticItem ToDiagnosticItem(Diagnostic diagnostic, string projectId)
    {
        string? filePath = null;
        int? startLine = null;
        int? startCharacter = null;
        int? endLine = null;
        int? endCharacter = null;

        var location = diagnostic.Location;
        if (location.IsInSource)
        {
            var span = location.GetLineSpan();
            filePath = span.Path;
            startLine = span.StartLinePosition.Line + 1;
            startCharacter = span.StartLinePosition.Character;
            endLine = span.EndLinePosition.Line + 1;
            endCharacter = span.EndLinePosition.Character;
        }

        return new DiagnosticItem(
            Id: diagnostic.Id,
            Severity: diagnostic.Severity.ToString(),
            Message: diagnostic.GetMessage(),
            FilePath: filePath,
            StartLine: startLine,
            StartCharacter: startCharacter,
            EndLine: endLine,
            EndCharacter: endCharacter,
            ProjectId: projectId);
    }

}
