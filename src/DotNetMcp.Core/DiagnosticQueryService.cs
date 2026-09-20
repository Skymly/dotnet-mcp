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

        var adapter = _languages.ForProjectId(session, projectId);
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
        if (!SoftBudgetPage.TryReadOffset(cursor, epoch, "project_diagnostics", "*", out _, out var cursorError))
        {
            return (null, cursorError);
        }

        var budget = softBudget ?? _softBudgets.BatchDiagnostics;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var collected = new List<DiagnosticItem>();
        var stoppedEarly = false;
        var projectFailures = new List<string>();
        foreach (var project in session.Solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget > TimeSpan.Zero && started.Elapsed >= budget)
            {
                stoppedEarly = true;
                break;
            }

            var adapter = _languages.ForProject(project);
            if (adapter is null)
            {
                continue;
            }

            var projectId = project.Id.Id.ToString("D");
            string? projectCursor = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (budget > TimeSpan.Zero && started.Elapsed >= budget)
                {
                    stoppedEarly = true;
                    break;
                }

                var remaining = budget <= TimeSpan.Zero ? budget : budget - started.Elapsed;
                var (page, error) = await adapter.GetProjectDiagnosticsAsync(
                        session,
                        projectId,
                        limit: LanguageAdapters.MaxMemberPageLimit,
                        cursor: projectCursor,
                        softBudget: remaining,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (error is not null)
                {
                    collected.Add(new DiagnosticItem(
                        error.Code,
                        "Error",
                        $"Project '{project.Name}' diagnostics failed: {error.Message}",
                        project.FilePath,
                        StartLine: null,
                        StartCharacter: null,
                        EndLine: null,
                        EndCharacter: null,
                        projectId));
                    projectFailures.Add(project.Name);
                    break;
                }

                collected.AddRange(page!.Items);
                if (!page.Truncated || string.IsNullOrWhiteSpace(page.NextCursor))
                {
                    break;
                }

                if (page.Message.Contains("Soft budget", StringComparison.OrdinalIgnoreCase))
                {
                    stoppedEarly = true;
                    break;
                }

                projectCursor = page.NextCursor;
            }

            if (stoppedEarly)
            {
                break;
            }
        }

        var (paged, pageError) = SoftBudgetPage.Page(
            collected,
            epoch,
            budgetHit: stoppedEarly,
            cursor,
            pageLimit,
            "project_diagnostics",
            "*",
            "Workspace has no error or warning diagnostics.",
            "Batch diagnostics page complete.",
            "the diagnostics list",
            scanIncomplete: stoppedEarly);
        if (paged is not null && projectFailures.Count > 0)
        {
            var failed = string.Join(", ", projectFailures);
            paged = paged with
            {
                Message = paged.Message +
                    $" One or more projects failed to produce diagnostics ({failed}); those rows are not a clean project."
            };
        }

        return (paged, pageError);
    }

    private static int ClampLimit(int? limit)
    {
        if (limit is null or <= 0)
        {
            return LanguageAdapters.DefaultMemberPageLimit;
        }

        return Math.Min(limit.Value, LanguageAdapters.MaxMemberPageLimit);
    }
}
