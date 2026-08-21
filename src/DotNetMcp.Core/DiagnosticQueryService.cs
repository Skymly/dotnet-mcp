namespace DotNetMcp.Core;

public sealed class DiagnosticQueryService
{
    public static readonly TimeSpan SingleProjectCompileSoftBudget =
        SoftBudgetOptions.Default.SingleProjectCompile;

    private readonly SoftBudgetOptions _softBudgets;
    private readonly LanguageAdapters _languages;

    public DiagnosticQueryService(SoftBudgetOptions? softBudgets = null, LanguageAdapters? languages = null)
    {
        _softBudgets = softBudgets ?? SoftBudgetOptions.Default;
        _languages = languages ?? new LanguageAdapters([new RoslynLanguageAdapter(new GeneratorQueryService(), _softBudgets)]);
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
            return await GetBatchDiagnosticsAsync(session, limit, cursor, softBudget, cancellationToken)
                .ConfigureAwait(false);
        }

        var adapter = _languages.ForProjectId(session.Solution, projectId);
        if (adapter is null)
        {
            return (null, new ProjectNotFoundError(
                $"No project with projectId '{projectId}' is in the ready workspace.",
                "Call workspace_list_projects for valid projectId values, then retry project_diagnostics."));
        }

        return await adapter
            .GetProjectDiagnosticsAsync(session, projectId, limit, cursor, softBudget, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(PagedResult<DiagnosticItem>? Success, SymbolQueryError? Error)> GetBatchDiagnosticsAsync(
        IWorkspaceSession session,
        int? limit,
        string? cursor,
        TimeSpan? softBudget,
        CancellationToken cancellationToken)
    {
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

        var budget = softBudget ?? _softBudgets.BatchDiagnostics;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var collected = new List<DiagnosticItem>();
        var stoppedEarly = false;
        foreach (var project in session.Solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (started.Elapsed >= budget)
            {
                stoppedEarly = true;
                break;
            }

            var adapter = _languages.ForProject(project);
            if (adapter is null)
            {
                continue;
            }

            var remaining = budget - started.Elapsed;
            var (page, error) = await adapter.GetProjectDiagnosticsAsync(
                    session,
                    project.Id.Id.ToString("D"),
                    limit: 100,
                    cursor: null,
                    softBudget: remaining,
                    cancellationToken)
                .ConfigureAwait(false);
            if (error is not null)
            {
                continue;
            }

            collected.AddRange(page!.Items);
        }

        if (offset > collected.Count)
        {
            return (null, new StaleCursorError(
                "Cursor offset is past the end of the diagnostics list.",
                "Call project_diagnostics again without a cursor to start a fresh page."));
        }

        var slice = collected.Skip(offset).Take(pageLimit).ToList();
        var nextOffset = offset + slice.Count;
        var truncated = nextOffset < collected.Count || stoppedEarly;
        string? nextCursor = truncated ? MemberPageCursor.Encode(epoch, nextOffset) : null;
        var message = truncated
            ? "Batch diagnostics truncated; pass nextCursor to project_diagnostics to continue (do not restart from the first page)."
            : collected.Count == 0
                ? "Workspace has no error or warning diagnostics."
                : "Batch diagnostics page complete.";
        return (new PagedResult<DiagnosticItem>(slice, truncated, nextCursor, message), null);
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
