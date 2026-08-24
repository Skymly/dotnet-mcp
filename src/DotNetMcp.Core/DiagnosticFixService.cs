using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Core;

/// <summary>
/// First-party / parameterless CodeFix host (Spike S6). No Visual Studio MEF catalog.
/// </summary>
public sealed class DiagnosticFixService
{
    private readonly LanguageAdapters _languages;
    private readonly SoftBudgetOptions _budgets;

    public DiagnosticFixService(LanguageAdapters? languages = null, SoftBudgetOptions? budgets = null)
    {
        _languages = languages ?? new LanguageAdapters([new RoslynLanguageAdapter(new GeneratorQueryService())]);
        _budgets = budgets ?? SoftBudgetOptions.Default;
    }

    public async Task<(DiagnosticFixListSuccess? Success, SymbolQueryError? Error)> ListFixesAsync(
        IWorkspaceSession session,
        string projectId,
        string diagnosticId,
        string? filePath,
        int? startLine,
        int? startCharacter,
        int? endLine,
        int? endCharacter,
        CancellationToken cancellationToken = default)
    {
        var (document, diagnostic, error) = await ResolveOccurrenceAsync(
                session, projectId, diagnosticId, filePath, startLine, startCharacter, endLine, endCharacter, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        var actions = await CollectActionsAsync(document!, diagnostic!, cancellationToken).ConfigureAwait(false);
        var items = actions
            .Select((action, index) => new DiagnosticFixItem(index, action.Title, action.EquivalenceKey))
            .ToArray();
        return (new DiagnosticFixListSuccess(items), null);
    }

    public async Task<(DiagnosticFixPreviewDraft? Draft, SymbolQueryError? Error)> BuildPreviewAsync(
        IWorkspaceSession session,
        string projectId,
        string diagnosticId,
        string? filePath,
        int? startLine,
        int? startCharacter,
        int? endLine,
        int? endCharacter,
        int fixIndex,
        string? scope = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedScope = NormalizeScope(scope, out var scopeError);
        if (scopeError is not null)
        {
            return (null, scopeError);
        }

        var (document, diagnostic, error) = await ResolveOccurrenceAsync(
                session, projectId, diagnosticId, filePath, startLine, startCharacter, endLine, endCharacter, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        var actions = await CollectActionsAsync(document!, diagnostic!, cancellationToken).ConfigureAwait(false);
        if (fixIndex < 0 || fixIndex >= actions.Count)
        {
            return (null, new FixIndexOutOfRangeError(
                $"fixIndex {fixIndex} is out of range for {actions.Count} available fix(es).",
                "Call diagnostics_list_fixes and pass a fixIndex from that list."));
        }

        var chosen = actions[fixIndex];
        Solution? changed;
        if (normalizedScope == DiagnosticFixScopes.Document || normalizedScope == DiagnosticFixScopes.Project)
        {
            if (string.IsNullOrWhiteSpace(chosen.EquivalenceKey))
            {
                return (null, new FixAllUnavailableError(
                    $"This fix has no EquivalenceKey, so {normalizedScope}-scope Fix all is unavailable.",
                    "Call diagnostics_preview_fix with scope=occurrence, or pick a fix that reports an EquivalenceKey."));
            }

            if (normalizedScope == DiagnosticFixScopes.Project)
            {
                var (projectSolution, projectError) = await ApplyProjectScopeAsync(
                        document!, diagnostic!.Id, chosen.EquivalenceKey, cancellationToken)
                    .ConfigureAwait(false);
                if (projectError is not null)
                {
                    return (null, projectError);
                }

                changed = projectSolution;
            }
            else
            {
                changed = await ApplyDocumentScopeAsync(
                        document!, diagnostic!.Id, chosen.EquivalenceKey, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            changed = await CodeActionDocuments.ApplyActionAsync(chosen, cancellationToken).ConfigureAwait(false);
        }

        var (slices, sliceError) = await CodeActionDocuments.ToHandwrittenSlicesAsync(
                session.Solution,
                changed,
                () => new FixApplyFailedError(
                    $"CodeFix '{chosen.Title}' did not produce a handwritten document change.",
                    "Pick another fixIndex from diagnostics_list_fixes, or fix the code without this tool."),
                () => new GeneratedDocumentFixRefusedError(
                    "This Diagnostic fix would change a generated document.",
                    "Change the generator input (handwritten source / attribute) instead of applying a fix to generated output."),
                cancellationToken)
            .ConfigureAwait(false);
        if (sliceError is not null)
        {
            return (null, sliceError);
        }

        return (new DiagnosticFixPreviewDraft(
            chosen.Title,
            chosen.EquivalenceKey,
            normalizedScope,
            slices!,
            InvalidatedHandles: []), null);
    }

    private async Task<(Document? Document, Diagnostic? Diagnostic, SymbolQueryError? Error)> ResolveOccurrenceAsync(
        IWorkspaceSession session,
        string projectId,
        string diagnosticId,
        string? filePath,
        int? startLine,
        int? startCharacter,
        int? endLine,
        int? endCharacter,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return (null, null, new ProjectNotFoundError(
                "projectId is required to locate a diagnostic occurrence.",
                "Call workspace_list_projects, then project_diagnostics, then pass that projectId."));
        }

        if (string.IsNullOrWhiteSpace(diagnosticId))
        {
            return (null, null, new DiagnosticNotFoundError(
                "diagnosticId is required.",
                "Call project_diagnostics and pass the Id of the occurrence to fix."));
        }

        var adapter = _languages.ForProjectId(session, projectId);
        if (adapter is null)
        {
            return (null, null, new ProjectNotFoundError(
                $"No project with projectId '{projectId}' is in the ready workspace.",
                "Call workspace_list_projects for valid projectId values."));
        }

        if (!adapter.SupportsDiagnosticFix)
        {
            return (null, null, new FixLanguageNotSupportedError(
                "Diagnostic fix is not available for this language.",
                "Call diagnostics_list_fixes on a C# or VB project."));
        }

        var project = session.Solution.Projects.FirstOrDefault(p =>
            string.Equals(p.Id.Id.ToString("D"), projectId, StringComparison.OrdinalIgnoreCase));
        if (project is null)
        {
            return (null, null, new ProjectNotFoundError(
                $"No project with projectId '{projectId}' is in the ready workspace.",
                "Call workspace_list_projects for valid projectId values."));
        }

        Compilation compilation;
        try
        {
            compilation = await session.GetCompilationAsync(project.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return (null, null, new CompilationUnavailableError(
                ex.Message,
                "Retry after workspace_status is ready; confirm the projectId with workspace_list_projects."));
        }

        var matches = compilation.GetDiagnostics()
            .Where(d => string.Equals(d.Id, diagnosticId, StringComparison.Ordinal))
            .Where(d => MatchesLocator(d, filePath, startLine, startCharacter, endLine, endCharacter))
            .ToList();

        if (matches.Count == 0)
        {
            return (null, null, new DiagnosticNotFoundError(
                $"No diagnostic '{diagnosticId}' matched the given locator in project '{projectId}'.",
                "Call project_diagnostics and pass the exact Id / filePath / span from that result."));
        }

        if (matches.Count > 1)
        {
            return (null, null, new DiagnosticAmbiguousError(
                $"Diagnostic '{diagnosticId}' matched {matches.Count} occurrences; pass a tighter filePath/span.",
                "Include filePath and startLine/startCharacter from project_diagnostics to disambiguate."));
        }

        var diagnostic = matches[0];
        if (!diagnostic.Location.IsInSource || diagnostic.Location.SourceTree is null)
        {
            return (null, null, new DiagnosticNotFoundError(
                $"Diagnostic '{diagnosticId}' is not in source.",
                "Pick a source diagnostic from project_diagnostics."));
        }

        var document = project.GetDocument(diagnostic.Location.SourceTree)
                       ?? project.Documents.FirstOrDefault(d =>
                           PathsEqual(d.FilePath, diagnostic.Location.GetLineSpan().Path));
        if (document is null)
        {
            return (null, null, new GeneratedDocumentFixRefusedError(
                "This diagnostic is on a generated document.",
                "Change the generator input instead of applying a Diagnostic fix to generated output."));
        }

        return (document, diagnostic, null);
    }

    private static bool MatchesLocator(
        Diagnostic diagnostic,
        string? filePath,
        int? startLine,
        int? startCharacter,
        int? endLine,
        int? endCharacter)
    {
        if (!diagnostic.Location.IsInSource)
        {
            return false;
        }

        var span = diagnostic.Location.GetLineSpan();
        if (!string.IsNullOrWhiteSpace(filePath) && !PathsEqual(span.Path, filePath))
        {
            return false;
        }

        var actualStartLine = span.StartLinePosition.Line + 1;
        var actualStartChar = span.StartLinePosition.Character;
        var actualEndLine = span.EndLinePosition.Line + 1;
        var actualEndChar = span.EndLinePosition.Character;

        if (startLine is not null && startLine.Value != actualStartLine)
        {
            return false;
        }

        if (startCharacter is not null && startCharacter.Value != actualStartChar)
        {
            return false;
        }

        if (endLine is not null && endLine.Value != actualEndLine)
        {
            return false;
        }

        if (endCharacter is not null && endCharacter.Value != actualEndChar)
        {
            return false;
        }

        return true;
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path.Replace('/', Path.DirectorySeparatorChar);
        }
    }

    private static string NormalizeScope(string? scope, out SymbolQueryError? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(scope))
        {
            return DiagnosticFixScopes.Occurrence;
        }

        if (string.Equals(scope, DiagnosticFixScopes.Occurrence, StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticFixScopes.Occurrence;
        }

        if (string.Equals(scope, DiagnosticFixScopes.Document, StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticFixScopes.Document;
        }

        if (string.Equals(scope, DiagnosticFixScopes.Project, StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticFixScopes.Project;
        }

        error = new FixAllUnavailableError(
            $"Unknown scope '{scope}'.",
            "Pass scope=occurrence, scope=document, or scope=project.");
        return DiagnosticFixScopes.Occurrence;
    }

    private static async Task<IReadOnlyList<CodeAction>> CollectActionsAsync(
        Document document,
        Diagnostic diagnostic,
        CancellationToken cancellationToken)
    {
        var providers = CodeActionDocuments.GetProviders<CodeFixProvider>(document.Project.Language);
        var actions = new List<CodeAction>();
        foreach (var provider in providers)
        {
            if (!provider.FixableDiagnosticIds.Contains(diagnostic.Id))
            {
                continue;
            }

            try
            {
                var context = new CodeFixContext(
                    document,
                    diagnostic,
                    (action, _) => actions.AddRange(CodeActionDocuments.Flatten(action)),
                    cancellationToken);
                await provider.RegisterCodeFixesAsync(context).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Skip providers that require a full IDE host.
            }
        }

        return actions
            .DistinctBy(a => (a.Title, a.EquivalenceKey))
            .OrderBy(a => a.Title, StringComparer.Ordinal)
            .ThenBy(a => a.EquivalenceKey ?? string.Empty, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<Solution?> ApplyDocumentScopeAsync(
        Document document,
        string diagnosticId,
        string equivalenceKey,
        CancellationToken cancellationToken)
    {
        var currentSolution = document.Project.Solution;
        var currentDocId = document.Id;
        var applied = false;

        for (var i = 0; i < 32; i++)
        {
            var currentDoc = currentSolution.GetDocument(currentDocId);
            if (currentDoc is null)
            {
                break;
            }

            var compilation = await currentDoc.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            var tree = await currentDoc.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null || tree is null)
            {
                break;
            }

            var nextOccurrence = compilation.GetDiagnostics()
                .Where(d => d.Location.SourceTree == tree && string.Equals(d.Id, diagnosticId, StringComparison.Ordinal))
                .OrderByDescending(d => d.Location.SourceSpan.Start)
                .FirstOrDefault();
            if (nextOccurrence is null)
            {
                break;
            }

            var actions = await CollectActionsAsync(currentDoc, nextOccurrence, cancellationToken).ConfigureAwait(false);
            var match = actions.FirstOrDefault(a =>
                string.Equals(a.EquivalenceKey, equivalenceKey, StringComparison.Ordinal));
            if (match is null)
            {
                break;
            }

            var next = await CodeActionDocuments.ApplyActionAsync(match, cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                break;
            }

            currentSolution = next;
            applied = true;
        }

        return applied ? currentSolution : null;
    }

    private async Task<(Solution? Solution, SymbolQueryError? Error)> ApplyProjectScopeAsync(
        Document document,
        string diagnosticId,
        string equivalenceKey,
        CancellationToken cancellationToken)
    {
        var currentSolution = document.Project.Solution;
        var projectId = document.Project.Id;
        var applied = 0;
        var deadline = DateTime.UtcNow + _budgets.FixAllProject;
        var cap = Math.Max(1, _budgets.FixAllProjectMaxApplications);

        while (applied < cap)
        {
            if (DateTime.UtcNow >= deadline)
            {
                return (null, new FixAllBudgetExceededError(
                    "Project-scope Fix all exceeded its time budget before every occurrence could be applied.",
                    "Retry with a longer DOTNET_MCP_BUDGET_FIXALL_PROJECT_MS, or apply scope=document per file."));
            }

            var project = currentSolution.GetProject(projectId);
            if (project is null)
            {
                break;
            }

            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                break;
            }

            Document? nextDoc = null;
            Diagnostic? nextOccurrence = null;
            foreach (var candidate in project.Documents)
            {
                var tree = await candidate.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                if (tree is null)
                {
                    continue;
                }

                var hit = compilation.GetDiagnostics()
                    .Where(d => d.Location.SourceTree == tree &&
                                string.Equals(d.Id, diagnosticId, StringComparison.Ordinal))
                    .OrderByDescending(d => d.Location.SourceSpan.Start)
                    .FirstOrDefault();
                if (hit is null)
                {
                    continue;
                }

                nextDoc = candidate;
                nextOccurrence = hit;
                break;
            }

            if (nextDoc is null || nextOccurrence is null)
            {
                break;
            }

            var actions = await CollectActionsAsync(nextDoc, nextOccurrence, cancellationToken).ConfigureAwait(false);
            var match = actions.FirstOrDefault(a =>
                string.Equals(a.EquivalenceKey, equivalenceKey, StringComparison.Ordinal));
            if (match is null)
            {
                break;
            }

            var next = await CodeActionDocuments.ApplyActionAsync(match, cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                break;
            }

            currentSolution = next;
            applied++;
        }

        if (applied >= cap)
        {
            var leftover = await ProjectHasRemainingAsync(
                    currentSolution.GetProject(projectId), diagnosticId, cancellationToken)
                .ConfigureAwait(false);
            if (leftover)
            {
                return (null, new FixAllBudgetExceededError(
                    $"Project-scope Fix all hit the application cap ({cap}) before every occurrence could be applied.",
                    "Apply scope=document per file, or raise the host FixAllProjectMaxApplications cap."));
            }
        }

        return applied > 0 ? (currentSolution, null) : (null, null);
    }

    private static async Task<bool> ProjectHasRemainingAsync(
        Project? project,
        string diagnosticId,
        CancellationToken cancellationToken)
    {
        if (project is null)
        {
            return false;
        }

        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null)
        {
            return false;
        }

        foreach (var candidate in project.Documents)
        {
            var tree = await candidate.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            if (tree is null)
            {
                continue;
            }

            if (compilation.GetDiagnostics().Any(d =>
                    d.Location.SourceTree == tree &&
                    string.Equals(d.Id, diagnosticId, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}
