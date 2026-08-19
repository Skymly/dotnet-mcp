using Microsoft.CodeAnalysis;

namespace DotNetMcp.Core;

public sealed class DiagnosticQueryService
{
    public static readonly TimeSpan SingleProjectCompileSoftBudget =
        SoftBudgetOptions.Default.SingleProjectCompile;

    private readonly SoftBudgetOptions _softBudgets;
    private readonly IFSharpSymbolQuery? _fsharp;

    public DiagnosticQueryService(SoftBudgetOptions? softBudgets = null, IFSharpSymbolQuery? fsharp = null)
    {
        _softBudgets = softBudgets ?? SoftBudgetOptions.Default;
        _fsharp = fsharp;
    }

    public async Task<(PagedResult<DiagnosticItem>? Success, SymbolQueryError? Error)> GetProjectDiagnosticsAsync(
        IWorkspaceSession session,
        string projectId,
        int? limit = null,
        string? cursor = null,
        TimeSpan? softBudget = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return (null, new ProjectNotFoundError(
                "projectId is required.",
                "Call workspace_list_projects for valid projectId values, then retry project_diagnostics."));
        }

        var fsharpProject = session.Solution.Projects.FirstOrDefault(p =>
            p.Language == LanguageNames.FSharp &&
            string.Equals(p.Id.Id.ToString("D"), projectId, StringComparison.OrdinalIgnoreCase));
        if (fsharpProject is not null)
        {
            if (_fsharp is null)
            {
                return (null, new ProjectNotFoundError(
                    $"No project with projectId '{projectId}' is in the ready workspace.",
                    "Call workspace_list_projects for valid projectId values, then retry project_diagnostics."));
            }

            return await _fsharp
                .GetProjectDiagnosticsAsync(session, projectId, limit, cursor, softBudget, cancellationToken)
                .ConfigureAwait(false);
        }

        var project = session.Solution.Projects
            .Where(p => SymbolQueryService.IsSupportedRoslynLanguage(p.Language))
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
                // Align with FindRefs: soft budget yields a continuable page, not SoftBudgetExceeded.
                var softMessage =
                    $"Soft budget reached after 0 item(s). Pass nextCursor to continue; do not retry from scratch.";
                return (new PagedResult<DiagnosticItem>(
                    Array.Empty<DiagnosticItem>(),
                    Truncated: true,
                    NextCursor: MemberPageCursor.Encode(epoch, offset),
                    Message: softMessage), null);
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
            .Select(d => ToItem(d, projectIdString))
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .ThenBy(d => d.FilePath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(d => d.StartLine ?? -1)
            .ThenBy(d => d.StartCharacter ?? -1)
            .ThenBy(d => d.Message, StringComparer.Ordinal)
            .ToList();

        if (offset > diagnostics.Count)
        {
            return (null, new StaleCursorError(
                "Cursor offset is past the end of the diagnostics list.",
                "Call project_diagnostics again without a cursor to start a fresh page."));
        }

        var slice = diagnostics.Skip(offset).Take(pageLimit).ToList();
        var nextOffset = offset + slice.Count;
        var truncated = nextOffset < diagnostics.Count;
        string? nextCursor = truncated ? MemberPageCursor.Encode(epoch, nextOffset) : null;

        var message = truncated
            ? "Results truncated; pass nextCursor to project_diagnostics to continue (do not restart from the first page)."
            : diagnostics.Count == 0
                ? "Project has no error or warning diagnostics."
                : "Diagnostics page complete.";

        return (new PagedResult<DiagnosticItem>(slice, truncated, nextCursor, message), null);
    }

    private static DiagnosticItem ToItem(Diagnostic diagnostic, string projectId)
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
            // Roslyn LinePosition: Line is 0-based; expose 1-based lines / 0-based characters.
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

    private static int ClampLimit(int? limit)
    {
        if (limit is null or <= 0)
        {
            return SymbolQueryService.DefaultMemberPageLimit;
        }

        return Math.Min(limit.Value, SymbolQueryService.MaxMemberPageLimit);
    }
}
