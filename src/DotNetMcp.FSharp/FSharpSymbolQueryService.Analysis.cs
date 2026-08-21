using System.Diagnostics;
using DotNetMcp.Core;
using FSharp.Compiler.CodeAnalysis;
using FSharp.Compiler.Symbols;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.FSharp.Core;
using FcsRange = global::FSharp.Compiler.Text.Range;

namespace DotNetMcp.FSharp;

public sealed partial class FSharpSymbolQueryService
{
    public async Task<(PagedResult<ReferenceLocationItem>? Success, SymbolQueryError? Error)> FindReferencesAsync(
        IWorkspaceSession session,
        string handle,
        bool entireSolution = false,
        int? limit = null,
        string? cursor = null,
        TimeSpan? softBudget = null,
        CancellationToken cancellationToken = default)
    {
        var (item, project, check, error) = await TryResolveWithCheckAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        var pageLimit = limit is null or < 1
            ? SymbolQueryService.DefaultMemberPageLimit
            : Math.Min(limit.Value, SymbolQueryService.MaxMemberPageLimit);
        var budget = softBudget ?? (entireSolution
            ? _softBudgets.FindRefsEntireSolution
            : _softBudgets.FindRefsScoped);
        var clock = Stopwatch.StartNew();

        var hits = new List<ReferenceLocationItem>();
        var truncatedByBudget = false;
        if (check is not null)
        {
            foreach (var use in check.GetAllUsesOfAllSymbols(null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clock.Elapsed >= budget)
                {
                    truncatedByBudget = true;
                    break;
                }

                if (!SameSymbol(use.Symbol, item!))
                {
                    continue;
                }

                hits.Add(ToReference(
                    item!.ProjectId,
                    use.FileName,
                    use.Range,
                    use.IsFromDefinition
                        ? ReferenceLocationKind.Definition
                        : ReferenceLocationKind.Reference));
            }
        }

        var scopeDocs = FindRefsScopes.DocumentsForScope(
            session.Solution,
            project!,
            entireSolution ? FindRefsScopeKind.EntireSolution : FindRefsScopeKind.DependencyClosure);

        foreach (var roslynProject in scopeDocs.Select(d => d.Project).Distinct())
        {
            if (roslynProject.Language is not LanguageNames.CSharp and not LanguageNames.VisualBasic)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed >= budget)
            {
                truncatedByBudget = true;
                break;
            }

            Compilation compilation;
            try
            {
                compilation = await session.GetCompilationAsync(roslynProject.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            var imported = compilation.GetTypeByMetadataName(item!.SignatureQualifiedName)
                           ?? compilation.GetTypeByMetadataName(item.SignatureQualifiedName.Replace(".", "+", StringComparison.Ordinal));
            if (imported is null)
            {
                continue;
            }

            var refs = await SymbolFinder.FindReferencesAsync(
                    imported,
                    session.Solution,
                    scopeDocs,
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var referenced in refs)
            {
                foreach (var loc in referenced.Locations)
                {
                    var span = loc.Location.SourceSpan;
                    hits.Add(new ReferenceLocationItem(
                        DeclarationAvailability.InSource,
                        SymbolOrigin.Handwritten,
                        loc.Document.FilePath,
                        span.Start,
                        span.Length,
                        roslynProject.Id.Id.ToString("D"),
                        ReferenceLocationKind.Reference));
                }
            }
        }

        return Page(hits, session.Epoch, entireSolution, pageLimit, cursor, "symbol_find_references", truncatedByBudget);
    }

    public async Task<(PagedResult<ImplementationItem>? Success, SymbolQueryError? Error)> FindImplementationsAsync(
        IWorkspaceSession session,
        string handle,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var (item, project, _, error) = await TryResolveWithCheckAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        if (!item!.IsContainer)
        {
            return (null, new SymbolNotFoundError(
                "Handle does not refer to a named type; implementations require a type SymbolHandle.",
                "Call symbol_resolve for a type name/FQN, then call symbol_find_implementations with that handle."));
        }

        var catalog = await CatalogAsync(project!, cancellationToken).ConfigureAwait(false);
        var impls = catalog
            .Where(candidate => candidate.IsContainer && candidate.SignatureQualifiedName != item.SignatureQualifiedName)
            .Where(candidate =>
                item.IsInterface
                    ? (candidate.InterfaceNames ?? []).Contains(item.SignatureQualifiedName, StringComparer.Ordinal)
                    : InheritsFrom(candidate, item.SignatureQualifiedName, catalog))
            .Select(candidate =>
            {
                var success = ToSuccess(candidate);
                return new ImplementationItem(success.Handle, success.Summary, candidate.Locations);
            })
            .ToList();

        return Page(impls, session.Epoch, pageLimit: limit, cursor, "symbol_find_implementations",
            emptyMessage: "No implementations were found.");
    }

    public async Task<(PagedResult<HierarchyItem>? Success, SymbolQueryError? Error)> GetTypeHierarchyAsync(
        IWorkspaceSession session,
        string handle,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var (item, project, _, error) = await TryResolveWithCheckAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        if (!item!.IsContainer)
        {
            return (null, new SymbolNotFoundError(
                "Handle does not refer to a named type; type hierarchy requires a type SymbolHandle.",
                "Call symbol_resolve for a type name/FQN, then call symbol_type_hierarchy with that handle."));
        }

        var catalog = await CatalogAsync(project!, cancellationToken).ConfigureAwait(false);
        var byName = catalog.Where(c => c.IsContainer)
            .ToDictionary(c => c.SignatureQualifiedName, StringComparer.Ordinal);
        var chain = new List<HierarchyItem>();

        var current = item.BaseTypeName;
        while (!string.IsNullOrWhiteSpace(current) && byName.TryGetValue(current, out var parent))
        {
            var success = ToSuccess(parent);
            chain.Add(new HierarchyItem(HierarchyRelationKind.BaseType, success.Handle, success.Summary));
            current = parent.BaseTypeName;
        }

        foreach (var ifaceName in item.InterfaceNames ?? [])
        {
            if (!byName.TryGetValue(ifaceName, out var iface))
            {
                continue;
            }

            var success = ToSuccess(iface);
            chain.Add(new HierarchyItem(HierarchyRelationKind.Interface, success.Handle, success.Summary));
        }

        return Page(chain, session.Epoch, pageLimit: limit, cursor, "symbol_type_hierarchy",
            emptyMessage: "Type has no base types or interfaces.");
    }

    public async Task<(PagedResult<CallerLocationItem>? Success, SymbolQueryError? Error)> FindCallersAsync(
        IWorkspaceSession session,
        string handle,
        int? limit = null,
        string? cursor = null,
        TimeSpan? softBudget = null,
        CancellationToken cancellationToken = default)
    {
        var (item, _, check, error) = await TryResolveWithCheckAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        if (item!.IsContainer)
        {
            return (null, new SymbolNotFoundError(
                "Handle does not refer to a method; callers require a method SymbolHandle.",
                "Call symbol_resolve for a method name/FQN, then call symbol_find_callers with that handle."));
        }

        var budget = softBudget ?? _softBudgets.FindRefsScoped;
        var clock = Stopwatch.StartNew();
        var hits = new List<CallerLocationItem>();
        var truncatedByBudget = false;
        if (check is not null)
        {
            foreach (var use in check.GetAllUsesOfAllSymbols(null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clock.Elapsed >= budget)
                {
                    truncatedByBudget = true;
                    break;
                }

                if (use.IsFromDefinition || !SameSymbol(use.Symbol, item))
                {
                    continue;
                }

                var loc = ToLocation(use.FileName, use.Range);
                hits.Add(new CallerLocationItem(
                    loc.DeclarationAvailability,
                    loc.Origin,
                    loc.FilePath,
                    loc.Start,
                    loc.Length,
                    item.ProjectId,
                    ToSuccess(item).Handle,
                    ToSuccess(item).Summary));
            }
        }

        return Page(hits, session.Epoch, limit, cursor, "symbol_find_callers", "No callers were found.", truncatedByBudget);
    }

    private async Task<(FSharpCatalogItem? Item, Microsoft.CodeAnalysis.Project? Project, FSharpCheckProjectResults? Check, SymbolQueryError? Error)>
        TryResolveWithCheckAsync(IWorkspaceSession session, string handle, CancellationToken cancellationToken)
    {
        if (!SymbolHandle.TryParse(handle, out var parsed, out var parseError) || parsed is null)
        {
            return (null, null, null, new InvalidSymbolHandleError(
                parseError ?? "Handle format or checksum is invalid.",
                "Call symbol_resolve with a name/FQN to obtain a fresh SymbolHandle; do not invent handles."));
        }

        if (!string.Equals(parsed.Language, SymbolQueryService.FSharpLanguage, StringComparison.Ordinal))
        {
            return (null, null, null, new InvalidSymbolHandleError(
                $"Unsupported language '{parsed.Language}'.",
                "Call symbol_resolve for an F# symbol to obtain a fsharp handle."));
        }

        var project = session.Solution.Projects.FirstOrDefault(p =>
            string.Equals(p.Id.Id.ToString("D"), parsed.ProjectId, StringComparison.OrdinalIgnoreCase));
        if (project is null || project.Language != LanguageNames.FSharp)
        {
            return (null, null, null, new SymbolNotFoundError(
                $"No F# project '{parsed.ProjectId}' is in the ready workspace.",
                "Call workspace_list_projects, then symbol_resolve for an F# symbol."));
        }

        var (catalog, check, _) = await CheckProjectAsync(project, cancellationToken).ConfigureAwait(false);
        catalog = FlattenCatalog(catalog).ToList();
        var hit = catalog.FirstOrDefault(item =>
            string.Equals(item.SignatureQualifiedName, parsed.SignatureQualifiedName, StringComparison.Ordinal) ||
            string.Equals(item.DisplayName, parsed.SignatureQualifiedName, StringComparison.Ordinal) ||
            item.SignatureQualifiedName.EndsWith("." + parsed.SignatureQualifiedName, StringComparison.Ordinal));
        if (hit is null)
        {
            return (null, project, check, new SymbolNotFoundError(
                $"Symbol '{parsed.SignatureQualifiedName}' was not found in project '{parsed.ProjectId}'.",
                "Call symbol_resolve with a name/FQN to obtain a fresh SymbolHandle."));
        }

        return (hit, project, check, null);
    }

    private static bool SameSymbol(FSharpSymbol symbol, FSharpCatalogItem item)
    {
        var name = symbol.FullName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = symbol.DisplayName;
        }

        if (string.Equals(name, item.SignatureQualifiedName, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(symbol.DisplayName, item.DisplayName, StringComparison.Ordinal))
        {
            return false;
        }

        if (!OptionModule.IsSome(symbol.DeclarationLocation))
        {
            return false;
        }

        var declaredFile = symbol.DeclarationLocation.Value.FileName;
        return item.Locations.Any(location => SameDocumentPath(location.FilePath, declaredFile));
    }

    private static bool InheritsFrom(FSharpCatalogItem candidate, string baseName, IReadOnlyList<FSharpCatalogItem> catalog)
    {
        var current = candidate.BaseTypeName;
        var guard = 0;
        while (!string.IsNullOrWhiteSpace(current) && guard++ < 16)
        {
            if (string.Equals(current, baseName, StringComparison.Ordinal))
            {
                return true;
            }

            current = catalog.FirstOrDefault(c => c.SignatureQualifiedName == current)?.BaseTypeName;
        }

        return false;
    }

    private ReferenceLocationItem ToReference(string projectId, string file, FcsRange range, string kind)
    {
        var loc = ToLocation(file, range);
        return new ReferenceLocationItem(
            loc.DeclarationAvailability,
            loc.Origin,
            loc.FilePath,
            loc.Start,
            loc.Length,
            projectId,
            kind);
    }

    private SymbolLocation ToLocation(string file, FcsRange range)
    {
        if (TryGetSnapshot(file, out var path, out var text))
        {
            var (start, length) = ToSpan(text, range);
            return new SymbolLocation(DeclarationAvailability.InSource, SymbolOrigin.Handwritten, path, start, length);
        }

        return new SymbolLocation(
            DeclarationAvailability.InSource,
            SymbolOrigin.Handwritten,
            string.IsNullOrWhiteSpace(file) ? null : file,
            null,
            null);
    }

    private static (PagedResult<T>? Success, SymbolQueryError? Error) Page<T>(
        IReadOnlyList<T> items,
        long epoch,
        int? pageLimit,
        string? cursor,
        string tool,
        string emptyMessage,
        bool truncatedByBudget = false)
    {
        var limit = pageLimit is null or < 1
            ? SymbolQueryService.DefaultMemberPageLimit
            : Math.Min(pageLimit.Value, SymbolQueryService.MaxMemberPageLimit);
        var offset = 0;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!MemberPageCursor.TryDecode(cursor, out var cursorEpoch, out offset, out var cursorError))
            {
                return (null, new StaleCursorError(
                    cursorError ?? "Cursor is invalid.",
                    $"Call {tool} again without a cursor to start a fresh page."));
            }

            if (cursorEpoch != epoch)
            {
                return (null, new StaleCursorError(
                    $"Cursor epoch {cursorEpoch} does not match workspace epoch {epoch}.",
                    $"Call {tool} again without a cursor; do not retry with the stale cursor."));
            }
        }

        if (offset > items.Count)
        {
            return (null, new StaleCursorError(
                "Cursor offset is past the end of the result list.",
                $"Call {tool} again without a cursor to start a fresh page."));
        }

        var slice = items.Skip(offset).Take(limit).ToList();
        var next = offset + slice.Count;
        var truncated = next < items.Count || truncatedByBudget;
        var message = truncated
            ? truncatedByBudget
                ? $"Soft budget reached after {slice.Count} item(s). Pass nextCursor to {tool} to continue; do not retry from scratch."
                : $"Results truncated; pass nextCursor to {tool} to continue (do not restart from the first page)."
            : items.Count == 0 ? emptyMessage : "Page complete.";
        return (new PagedResult<T>(
            slice,
            truncated,
            truncated ? MemberPageCursor.Encode(epoch, next) : null,
            message), null);
    }

    private static (PagedResult<ReferenceLocationItem>? Success, SymbolQueryError? Error) Page(
        IReadOnlyList<ReferenceLocationItem> items,
        long epoch,
        bool entireSolution,
        int pageLimit,
        string? cursor,
        string tool,
        bool truncatedByBudget = false)
    {
        var offset = 0;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!FindRefsPageCursor.TryDecode(cursor, out var cursorEpoch, out var cursorEntire, out var docIndex, out var locOffset, out var cursorError))
            {
                return (null, new StaleCursorError(
                    cursorError ?? "Cursor is invalid.",
                    $"Call {tool} again without a cursor to start a fresh page."));
            }

            if (cursorEpoch != epoch || cursorEntire != entireSolution)
            {
                return (null, new StaleCursorError(
                    "Cursor does not match the current workspace epoch or scope.",
                    $"Call {tool} again without a cursor; do not retry with the stale cursor."));
            }

            offset = docIndex + locOffset;
        }

        if (offset > items.Count)
        {
            return (null, new StaleCursorError(
                "Cursor offset is past the end of the result list.",
                $"Call {tool} again without a cursor to start a fresh page."));
        }

        var slice = items.Skip(offset).Take(pageLimit).ToList();
        var next = offset + slice.Count;
        var truncated = next < items.Count || truncatedByBudget;
        var message = truncated
            ? truncatedByBudget
                ? $"Soft budget reached after {slice.Count} item(s). Pass nextCursor to {tool} to continue; do not retry from scratch."
                : $"Results truncated; pass nextCursor to {tool} to continue (do not restart from the first page)."
            : items.Count == 0 ? "No references were found." : "Page complete.";
        return (new PagedResult<ReferenceLocationItem>(
            slice,
            truncated,
            truncated ? FindRefsPageCursor.Encode(epoch, entireSolution, next, 0) : null,
            message), null);
    }
}
