using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;

namespace DotNetMcp.Core;

public sealed partial class RoslynLanguageAdapter
{
    private static readonly ConditionalWeakTable<Compilation, DiagnosticProjection> SmallDiagnosticLists = new();

    public Task<(ProjectDiagnosticsResult? Success, SymbolQueryError? Error)> GetProjectDiagnosticsAsync(
        IWorkspaceSession session,
        string projectId,
        int? limit = null,
        string? cursor = null,
        TimeSpan? softBudget = null,
        CancellationToken cancellationToken = default) =>
        GetProjectDiagnosticsCoreAsync(session, projectId, ClampLimit(limit), cursor, softBudget, cancellationToken);

    internal Task<(ProjectDiagnosticsResult? Success, SymbolQueryError? Error)> GetUnpagedProjectDiagnosticsAsync(
        IWorkspaceSession session,
        string projectId,
        TimeSpan softBudget,
        CancellationToken cancellationToken) =>
        GetProjectDiagnosticsCoreAsync(session, projectId, int.MaxValue, cursor: null, softBudget, cancellationToken);

    private async Task<(ProjectDiagnosticsResult? Success, SymbolQueryError? Error)> GetProjectDiagnosticsCoreAsync(
        IWorkspaceSession session,
        string projectId,
        int pageLimit,
        string? cursor,
        TimeSpan? softBudget,
        CancellationToken cancellationToken)
    {
        var project = LanguageAdapters.FindProjectById(session.Solution, projectId);
        if (project is null || !OwnsProject(project))
        {
            return (null, new ProjectNotFoundError(
                $"No project with projectId '{projectId}' is in the ready workspace.",
                "Call workspace_list_projects for valid projectId values, then retry project_diagnostics."));
        }

        var epoch = session.Epoch;
        if (!SoftBudgetPage.TryReadOffset(
                cursor,
                epoch,
                "project_diagnostics",
                projectId,
                out var offset,
                out var cursorError))
        {
            return (null, cursorError);
        }

        // Structural degraded signals (issues #560 / #566): computed before the
        // compile budget so a budget-hit page still carries them.
        var unmaterializedReferences = ProjectReferenceMaterialization.FindUnmaterialized(project);
        var unresolvedAnalyzers = AnalyzerReferenceMaterialization.FindUnresolved(session, project);

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
                return (new ProjectDiagnosticsResult(
                    SoftBudgetPage.Finish(
                        Array.Empty<DiagnosticItem>(),
                        moreItems: true,
                        budgetHit: true,
                        () => MemberPageCursor.Encode(epoch, offset, "project_diagnostics", projectId),
                        "project_diagnostics",
                        "Project has no error or warning diagnostics."),
                    MissingMetadataDiagnosticCount: 0,
                    UnmaterializedProjectReferences: unmaterializedReferences,
                    UnresolvedAnalyzerReferences: unresolvedAnalyzers), null);
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

        var diagnostics = ReadCompilerDiagnostics(compilation, project.Id, cancellationToken);
        var (page, pageError) = SoftBudgetPage.Page(
            diagnostics.Items,
            epoch,
            budgetHit: false,
            cursor,
            pageLimit,
            "project_diagnostics",
            projectId,
            "Project has no error or warning diagnostics.",
            "Diagnostics page complete.",
            "the diagnostics list");
        return page is null
            ? (null, pageError)
            : (new ProjectDiagnosticsResult(
                page,
                diagnostics.MissingMetadataCount,
                unmaterializedReferences,
                unresolvedAnalyzers), null);
    }

    private static DiagnosticProjection ReadCompilerDiagnostics(
        Compilation compilation, ProjectId projectId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var culture = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        if (SmallDiagnosticLists.TryGetValue(compilation, out var cached) &&
            cached.ProjectId == projectId && cached.Culture.Equals(culture))
        {
            return cached;
        }

        var projectIdString = projectId.Id.ToString("D");
        var items = compilation.GetDiagnostics(cancellationToken)
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(d => ToDiagnosticItem(d, projectIdString))
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .ThenBy(d => d.FilePath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(d => d.Line ?? -1)
            .ThenBy(d => d.Column ?? -1)
            .ThenBy(d => d.Message, StringComparer.Ordinal)
            .ToArray();
        var projection = new DiagnosticProjection(projectId, culture, items, MissingMetadataDiagnostics.CountIn(items));

        // A compilation can stay warm for a long time. Retain at most one
        // small projection and continue reporting live reference evidence separately.
        if (items.Length <= MaxMemberPageLimit)
        {
            SmallDiagnosticLists.AddOrUpdate(compilation, projection);
        }

        return projection;
    }

    private sealed record DiagnosticProjection(
        ProjectId ProjectId,
        (CultureInfo Culture, CultureInfo UICulture) Culture,
        IReadOnlyList<DiagnosticItem> Items,
        int MissingMetadataCount);

    private static DiagnosticItem ToDiagnosticItem(Diagnostic diagnostic, string projectId)
    {
        string? filePath = null;
        int? line = null;
        int? column = null;
        int? endLine = null;
        int? endColumn = null;

        var location = diagnostic.Location;
        if (location.IsInSource)
        {
            var span = location.GetLineSpan();
            filePath = span.Path;
            (line, column, endLine, endColumn) = SourcePositions.FromLineSpan(span);
        }

        return new DiagnosticItem(
            Id: diagnostic.Id,
            Severity: diagnostic.Severity.ToString(),
            Message: diagnostic.GetMessage(),
            FilePath: filePath,
            Line: line,
            Column: column,
            EndLine: endLine,
            EndColumn: endColumn,
            ProjectId: projectId);
    }

}
