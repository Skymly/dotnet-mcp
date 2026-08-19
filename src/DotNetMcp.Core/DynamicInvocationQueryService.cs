using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace DotNetMcp.Core;

public sealed class DynamicInvocationQueryService
{
    private readonly SoftBudgetOptions _softBudgets;

    public DynamicInvocationQueryService(SoftBudgetOptions? softBudgets = null)
    {
        _softBudgets = softBudgets ?? SoftBudgetOptions.Default;
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

        var project = session.Solution.Projects.FirstOrDefault(p =>
            SymbolQueryService.IsSupportedRoslynLanguage(p.Language) &&
            string.Equals(p.Id.Id.ToString("D"), projectId, StringComparison.OrdinalIgnoreCase));
        if (project is null)
        {
            return (null, new ProjectNotFoundError(
                $"No C#/VB project with projectId '{projectId}' is in the ready workspace.",
                "Call workspace_list_projects for valid projectId values, then retry project_list_dynamic_invocations."));
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
                    "Call project_list_dynamic_invocations again without a cursor to start a fresh page."));
            }

            if (cursorEpoch != epoch)
            {
                return (null, new StaleCursorError(
                    $"Cursor epoch {cursorEpoch} does not match workspace epoch {epoch}.",
                    "Call project_list_dynamic_invocations again without a cursor; do not retry with the stale cursor."));
            }
        }

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

        foreach (var document in project.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed >= budget)
            {
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
        }

        if (offset > items.Count)
        {
            return (null, new StaleCursorError(
                "Cursor offset is past the end of the dynamic invocation list.",
                "Call project_list_dynamic_invocations again without a cursor to start a fresh page."));
        }

        var slice = items.Skip(offset).Take(pageLimit).ToList();
        var next = offset + slice.Count;
        var truncated = next < items.Count || clock.Elapsed >= budget;
        return (new PagedResult<DynamicInvocationItem>(
            slice,
            truncated,
            truncated ? MemberPageCursor.Encode(epoch, next) : null,
            truncated
                ? "Results truncated; pass nextCursor to project_list_dynamic_invocations to continue (do not restart from the first page)."
                : items.Count == 0
                    ? "Project has no dynamic invocation sites."
                    : "Dynamic invocation page complete."), null);
    }

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
