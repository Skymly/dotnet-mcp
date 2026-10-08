using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace DotNetMcp.Core;

public sealed class DynamicInvocationQueryService
{
    private readonly SoftBudgetOptions _softBudgets;
    private readonly LanguageAdapters? _languages;

    public DynamicInvocationQueryService(SoftBudgetOptions? softBudgets = null, LanguageAdapters? languages = null)
    {
        _softBudgets = softBudgets ?? SoftBudgetOptions.Default;
        _languages = languages;
    }

    public async Task<(PagedResult<DynamicInvocationItem>? Success, SymbolQueryError? Error)> ListAsync(
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
                "Call workspace_list_projects for valid projectId values, then retry project_list_dynamic_invocations."));
        }

        var project = ResolveProject(session, projectId, out var resolveError);
        if (project is null)
        {
            return (null, resolveError ?? ProjectMissing(projectId));
        }

        var epoch = session.Epoch;
        var pageLimit = limit is null or < 1
            ? LanguageAdapters.DefaultMemberPageLimit
            : Math.Min(limit.Value, LanguageAdapters.MaxMemberPageLimit);
        Compilation compilation;
        try
        {
            compilation = await session.GetCompilationAsync(project.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return (null, new CompilationUnavailableError(
                ex.Message,
                "Retry project_list_dynamic_invocations; if it keeps failing, call workspace_list_projects and confirm the projectId."));
        }

        var budget = softBudget ?? _softBudgets.SingleProjectCompile;
        var clock = Stopwatch.StartNew();
        var items = new List<DynamicInvocationItem>();
        var projectIdString = project.Id.Id.ToString("D");
        var stoppedEarly = false;

        foreach (var document in project.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed >= budget)
            {
                stoppedEarly = true;
                break;
            }

            var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            if (tree is null)
            {
                continue;
            }

            var model = compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            var operation = model.GetOperation(root, cancellationToken);
            var ops = operation is null
                ? root.DescendantNodes().Select(n => model.GetOperation(n, cancellationToken)).Where(o => o is not null)
                : operation.DescendantsAndSelf();

            foreach (var op in ops!)
            {
                if (clock.Elapsed >= budget)
                {
                    stoppedEarly = true;
                    break;
                }

                switch (op)
                {
                    case IDynamicInvocationOperation invoke:
                        items.Add(ToItem(
                            "Invocation",
                            document.FilePath,
                            invoke.Syntax.Span.Start,
                            invoke.Syntax.Span.Length,
                            projectIdString,
                            StaticType(invoke.Operation),
                            invoke.Arguments.Select(a => StaticType(a is IArgumentOperation arg ? arg.Value : a)).ToArray()));
                        break;
                    case IDynamicMemberReferenceOperation member:
                        items.Add(ToItem(
                            "Member",
                            document.FilePath,
                            member.Syntax.Span.Start,
                            member.Syntax.Span.Length,
                            projectIdString,
                            StaticType(member.Instance),
                            []));
                        break;
                    case IDynamicIndexerAccessOperation indexer:
                        items.Add(ToItem(
                            "Indexer",
                            document.FilePath,
                            indexer.Syntax.Span.Start,
                            indexer.Syntax.Span.Length,
                            projectIdString,
                            StaticType(indexer.Operation),
                            indexer.Arguments.Select(a => StaticType(a is IArgumentOperation arg ? arg.Value : a)).ToArray()));
                        break;
                }
            }

            if (stoppedEarly)
            {
                break;
            }
        }

        return SoftBudgetPage.Page(
            items,
            epoch,
            budgetHit: clock.Elapsed >= budget,
            cursor,
            pageLimit,
            "project_list_dynamic_invocations",
            projectId,
            "Project has no dynamic invocation sites.",
            "Dynamic invocation page complete.",
            "the dynamic invocation list",
            scanIncomplete: stoppedEarly);
    }

    private Project? ResolveProject(IWorkspaceSession session, string projectId, out SymbolQueryError? error)
    {
        error = null;
        if (_languages is not null)
        {
            var adapter = _languages.ForProjectId(session, projectId);
            if (adapter is null)
            {
                error = ProjectMissing(projectId);
                return null;
            }

            if (!adapter.SupportsDynamicInvocations)
            {
                error = LanguageNotSupported();
                return null;
            }
        }

        var project = session.Solution.Projects.FirstOrDefault(p =>
            string.Equals(p.Id.Id.ToString("D"), projectId, StringComparison.OrdinalIgnoreCase));
        if (project is null)
        {
            error = session.FSharpSnapshot.FindProject(projectId) is null
                ? ProjectMissing(projectId)
                : LanguageNotSupported();
            return null;
        }

        if (!RoslynLanguageAdapter.IsSupportedRoslynLanguage(project.Language))
        {
            error = LanguageNotSupported();
            return null;
        }

        return project;
    }

    private static ProjectNotFoundError ProjectMissing(string projectId) =>
        new(
            $"No project with projectId '{projectId}' is in the ready workspace.",
            "Call workspace_list_projects for valid projectId values, then retry project_list_dynamic_invocations.");

    private static DynamicInvocationLanguageNotSupportedError LanguageNotSupported() =>
        new(
            "Dynamic invocation queries are not available for this language.",
            "Call project_list_dynamic_invocations on a C# or VB project.");
    private static DynamicInvocationItem ToItem(
        string kind,
        string? path,
        int start,
        int length,
        string projectId,
        string? receiver,
        IReadOnlyList<string?> args) =>
        new(kind, path, start, length, projectId, receiver, args);

    private static string? StaticType(IOperation? operation)
    {
        if (operation is null)
        {
            return null;
        }

        var type = operation.Type;
        if (type is null || type.TypeKind == TypeKind.Dynamic || type.SpecialType == SpecialType.System_Object && operation.Type?.IsAnonymousType != true)
        {
            if (type is { TypeKind: TypeKind.Dynamic })
            {
                return null;
            }
        }

        if (type is null || type.TypeKind == TypeKind.Dynamic)
        {
            return null;
        }

        return type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }
}
