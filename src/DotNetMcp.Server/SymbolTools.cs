using System.ComponentModel;
using DotNetMcp.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotNetMcp.Server;

[McpServerToolType]
public sealed class SymbolTools
{
    private readonly WorkspaceHost _workspaceHost;
    private readonly WorkspaceEdit _workspaceEdit;
    private readonly SymbolQueryService _symbols;
    private readonly RenamePreviewService _renames;
    private readonly IAuditLogger _audit;

    public SymbolTools(
        WorkspaceHost workspaceHost,
        WorkspaceEdit workspaceEdit,
        SymbolQueryService symbols,
        RenamePreviewService renames,
        IAuditLogger audit)
    {
        _workspaceHost = workspaceHost;
        _workspaceEdit = workspaceEdit;
        _symbols = symbols;
        _renames = renames;
        _audit = audit;
    }

    [McpServerTool(Name = "symbol_resolve"), Description(
        "Resolve a C# symbol by name or FQN in the ready workspace and return a verifiable SymbolHandle " +
        "plus a lightweight summary (no member tree). Optional projectId disambiguates multi-TFM / multi-project hits.")]
    public async Task<CallToolResult> SymbolResolve(
        [Description("Type or member name / FQN (e.g. SampleLib.Calculator).")]
        string name,
        [Description("Optional Roslyn projectId GUID string from workspace_list_projects.")]
        string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("symbol_resolve");

        if (!McpToolEnvelope.TryGetReadySession(_workspaceHost, out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _symbols
            .ResolveByNameAsync(session!, name, projectId, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return McpToolEnvelope.ErrorResult(McpToolEnvelope.ToPolicyError(error));
        }

        return McpToolEnvelope.OkResult(ToDto(success!));
    }

    [McpServerTool(Name = "symbol_summary"), Description(
        "Return a lightweight summary for a SymbolHandle. Distinguishes InvalidSymbolHandle " +
        "(format/checksum) from SymbolNotFound (handle valid but symbol gone).")]
    public async Task<CallToolResult> SymbolSummary(
        [Description("SymbolHandle from symbol_resolve: language:projectId:signature#checksum")]
        string handle,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("symbol_summary");

        if (!McpToolEnvelope.TryGetReadySession(_workspaceHost, out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _symbols
            .GetSummaryAsync(session!, handle, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return McpToolEnvelope.ErrorResult(McpToolEnvelope.ToPolicyError(error));
        }

        return McpToolEnvelope.OkResult(ToDto(success!));
    }

    [McpServerTool(Name = "symbol_goto_definition"), Description(
        "Navigate a SymbolHandle to its definition locations (file/span). Includes handwritten and " +
        "source-generated trees; Origin is Handwritten or SourceGenerator(Assembly::Type@Version) when in source.")]
    public async Task<CallToolResult> SymbolGotoDefinition(
        [Description("SymbolHandle from symbol_resolve: language:projectId:signature#checksum")]
        string handle,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("symbol_goto_definition");

        if (!McpToolEnvelope.TryGetReadySession(_workspaceHost, out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _symbols
            .GetDefinitionAsync(session!, handle, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return McpToolEnvelope.ErrorResult(McpToolEnvelope.ToPolicyError(error));
        }

        return McpToolEnvelope.OkResult(ToDefinitionDto(success!));
    }

    [McpServerTool(Name = "symbol_attribution"), Description(
        "Two-axis symbol attribution for a SymbolHandle: declaration availability plus Handwritten vs " +
        "SourceGenerator(identity) via public GeneratorDriver reconciliation (not FilePath heuristics). " +
        "Named types also return a members map keyed by signature-qualified name (partial/overload safe).")]
    public async Task<CallToolResult> SymbolAttribution(
        [Description("SymbolHandle from symbol_resolve: language:projectId:signature#checksum")]
        string handle,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("symbol_attribution");

        if (!McpToolEnvelope.TryGetReadySession(_workspaceHost, out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _symbols
            .GetAttributionAsync(session!, handle, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return McpToolEnvelope.ErrorResult(McpToolEnvelope.ToPolicyError(error));
        }

        return McpToolEnvelope.OkResult(ToAttributionDto(success!));
    }

    [McpServerTool(Name = "symbol_members"), Description(
        "List members of a type SymbolHandle with forced pagination. Cursors bind to the workspace " +
        "epoch and become stale when the workspace generation advances.")]
    public async Task<CallToolResult> SymbolMembers(
        [Description("Type SymbolHandle from symbol_resolve.")]
        string handle,
        [Description("Page size (default 50, max 100).")]
        int? limit = null,
        [Description("Opaque nextCursor from a previous symbol_members page.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("symbol_members");

        if (!McpToolEnvelope.TryGetReadySession(_workspaceHost, out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _symbols
            .GetMembersAsync(
                session!,
                handle,
                limit,
                cursor,
                cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return McpToolEnvelope.ErrorResult(McpToolEnvelope.ToPolicyError(error));
        }

        return McpToolEnvelope.OkResult(ToMembersDto(success!));
    }

    [McpServerTool(Name = "symbol_find_references"), Description(
        "Find references to a SymbolHandle. Default scope is the defining project's dependency closure; " +
        "pass entireSolution=true to search the whole solution. Soft time budget may truncate with nextCursor " +
        "(do not restart from scratch). Cursors bind to the workspace epoch.")]
    public async Task<CallToolResult> SymbolFindReferences(
        [Description("SymbolHandle from symbol_resolve: language:projectId:signature#checksum")]
        string handle,
        [Description("When true, search the entire solution; default false uses dependency closure.")]
        bool entireSolution = false,
        [Description("Page size (default 50, max 100).")]
        int? limit = null,
        [Description("Opaque nextCursor from a previous symbol_find_references page.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("symbol_find_references");

        if (!McpToolEnvelope.TryGetReadySession(_workspaceHost, out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _symbols
            .FindReferencesAsync(
                session!,
                handle,
                entireSolution,
                limit,
                cursor,
                softBudget: null,
                cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return McpToolEnvelope.ErrorResult(McpToolEnvelope.ToPolicyError(error));
        }

        return McpToolEnvelope.OkResult(ToFindReferencesDto(success!));
    }

    [McpServerTool(Name = "symbol_find_implementations"), Description(
        "Find types and members that implement or derive from a SymbolHandle (interfaces, abstract/virtual " +
        "members, and class inheritance). Results are paginated; cursors bind to the workspace epoch.")]
    public async Task<CallToolResult> SymbolFindImplementations(
        [Description("SymbolHandle from symbol_resolve: language:projectId:signature#checksum")]
        string handle,
        [Description("Page size (default 50, max 100).")]
        int? limit = null,
        [Description("Opaque nextCursor from a previous symbol_find_implementations page.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("symbol_find_implementations");

        if (!McpToolEnvelope.TryGetReadySession(_workspaceHost, out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _symbols
            .FindImplementationsAsync(session!, handle, limit, cursor, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return McpToolEnvelope.ErrorResult(McpToolEnvelope.ToPolicyError(error));
        }

        return McpToolEnvelope.OkResult(ToImplementationsDto(success!));
    }

    [McpServerTool(Name = "symbol_type_hierarchy"), Description(
        "Return a type SymbolHandle's base-type chain (immediate to root) then implemented interfaces, " +
        "paginated. Cursors bind to the workspace epoch and become stale when the workspace generation advances.")]
    public async Task<CallToolResult> SymbolTypeHierarchy(
        [Description("Type SymbolHandle from symbol_resolve.")]
        string handle,
        [Description("Page size (default 50, max 100).")]
        int? limit = null,
        [Description("Opaque nextCursor from a previous symbol_type_hierarchy page.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("symbol_type_hierarchy");

        if (!McpToolEnvelope.TryGetReadySession(_workspaceHost, out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _symbols
            .GetTypeHierarchyAsync(session!, handle, limit, cursor, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return McpToolEnvelope.ErrorResult(McpToolEnvelope.ToPolicyError(error));
        }

        return McpToolEnvelope.OkResult(ToHierarchyDto(success!));
    }

    [McpServerTool(Name = "symbol_find_callers"), Description(
        "Find direct call sites of a method SymbolHandle (shallow callers, not a full call graph). " +
        "Default scope is the defining project's dependency closure. Soft time budget may truncate with nextCursor. Cursors bind to the workspace epoch.")]
    public async Task<CallToolResult> SymbolFindCallers(
        [Description("Method SymbolHandle from symbol_resolve.")]
        string handle,
        [Description("Page size (default 50, max 100).")]
        int? limit = null,
        [Description("Opaque nextCursor from a previous symbol_find_callers page.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("symbol_find_callers");

        if (!McpToolEnvelope.TryGetReadySession(_workspaceHost, out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _symbols
            .FindCallersAsync(session!, handle, limit, cursor, softBudget: null, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return McpToolEnvelope.ErrorResult(McpToolEnvelope.ToPolicyError(error));
        }

        return McpToolEnvelope.OkResult(ToCallersDto(success!));
    }

    [McpServerTool(Name = "symbol_preview_rename"), Description(
        "Preview renaming a handwritten C# / VB / F# SymbolHandle. Returns a Workspace Edit (per-file old/new text, " +
        "handles that will become invalid) and an opaque previewId bound to the current workspace Epoch + TTL. " +
        "Does not write disk. SourceGenerator Origin is refused. There is no generic apply_edit / write / shell.")]
    public async Task<CallToolResult> SymbolPreviewRename(
        [Description("Handwritten C# / VB / F# SymbolHandle from symbol_resolve.")]
        string handle,
        [Description("New identifier (unqualified).")]
        string newName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("symbol_preview_rename");

        if (!McpToolEnvelope.TryGetReadySession(_workspaceHost, out var session, out var notReady))
        {
            return notReady!;
        }

        var (draft, error) = await _renames
            .BuildAsync(session!, handle, newName, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return McpToolEnvelope.ErrorResult(McpToolEnvelope.ToPolicyError(error));
        }

        var held = _workspaceEdit.Preview(new WorkspaceEditDraft(
            WorkspaceEditKind.RenamePreview,
            draft!.Documents.Select(d => new WorkspaceEditDocument(d.Path, d.OldText, d.NewText)).ToArray(),
            draft.InvalidatedHandles));
        if (held.Error is not null)
        {
            return McpToolEnvelope.ErrorResult(held.Error);
        }

        return McpToolEnvelope.OkResult(new SymbolPreviewRenameResultDto
        {
            PreviewId = held.Value!.PreviewId,
            Epoch = held.Value.Epoch,
            ExpiresAt = held.Value.ExpiresAt,
            OldHandle = draft.OldHandle,
            NewName = draft.NewName,
            Documents = ToDocumentDtos(held.Value.Documents),
            InvalidatedHandles = held.Value.InvalidatedHandles
        });
    }

    [McpServerTool(Name = "symbol_apply_rename"), Description(
        "Apply a still-valid C# / VB / F# rename preview. Writes only the documents listed in that preview, all of which " +
        "must already exist inside a trusted root. Uses WriteSuppression and advances the workspace Epoch. " +
        "There is no apply path that skips preview. Not a generic write / patch / shell tool.")]
    public Task<CallToolResult> SymbolApplyRename(
        [Description("previewId from symbol_preview_rename (current Epoch, unexpired).")]
        string previewId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("symbol_apply_rename");

        if (!McpToolEnvelope.TryGetReadySession(_workspaceHost, out _, out var notReady))
        {
            return Task.FromResult(notReady!);
        }

        var applied = _workspaceEdit.Apply(previewId, WorkspaceEditKind.RenamePreview);
        if (applied.Error is not null)
        {
            return Task.FromResult(McpToolEnvelope.ErrorResult(applied.Error));
        }

        return Task.FromResult(McpToolEnvelope.OkResult(new SymbolApplyRenameResultDto
        {
            PreviewId = applied.Value!.PreviewId,
            Epoch = applied.Value.Epoch,
            WrittenPaths = applied.Value.WrittenPaths,
            InvalidatedHandles = applied.Value.InvalidatedHandles
        }));
    }

    private static IReadOnlyList<RenameDocumentSliceDto> ToDocumentDtos(
        IReadOnlyList<WorkspaceEditDocument> documents) =>
        documents.Select(d => new RenameDocumentSliceDto
        {
            Path = d.Path,
            OldText = d.OldText,
            NewText = d.NewText
        }).ToArray();

    private static SymbolResolveResultDto ToDto(SymbolResolveSuccess success) => new()
    {
        Handle = success.Handle,
        Summary = ToSummaryDto(success.Summary)
    };

    private static SymbolDefinitionResultDto ToDefinitionDto(SymbolDefinitionSuccess success) => new()
    {
        Locations = success.Locations.Select(l => new SymbolLocationDto
        {
            DeclarationAvailability = l.DeclarationAvailability,
            Origin = l.Origin,
            FilePath = l.FilePath,
            Start = l.Start,
            Length = l.Length
        }).ToArray()
    };

    private static SymbolAttributionResultDto ToAttributionDto(SymbolAttributionSuccess success) => new()
    {
        DeclarationAvailability = success.Attribution.DeclarationAvailability,
        OriginKind = success.Attribution.OriginKind,
        Generator = success.Attribution.Generator is null
            ? null
            : new GeneratorIdentityDto
            {
                AssemblyName = success.Attribution.Generator.AssemblyName,
                TypeFullName = success.Attribution.Generator.TypeFullName,
                Version = success.Attribution.Generator.Version
            },
        Members = success.Members.ToDictionary(
            kv => kv.Key,
            kv => new SymbolAttributionDto
            {
                DeclarationAvailability = kv.Value.DeclarationAvailability,
                OriginKind = kv.Value.OriginKind,
                Generator = kv.Value.Generator is null
                    ? null
                    : new GeneratorIdentityDto
                    {
                        AssemblyName = kv.Value.Generator.AssemblyName,
                        TypeFullName = kv.Value.Generator.TypeFullName,
                        Version = kv.Value.Generator.Version
                    }
            },
            StringComparer.Ordinal)
    };

    private static SymbolMembersResultDto ToMembersDto(PagedResult<MemberListItem> page) => new()
    {
        Items = page.Items.Select(i => new MemberListItemDto
        {
            Handle = i.Handle,
            Summary = ToSummaryDto(i.Summary)
        }).ToArray(),
        Truncated = page.Truncated,
        NextCursor = page.NextCursor,
        Message = page.Message
    };

    private static SymbolFindReferencesResultDto ToFindReferencesDto(PagedResult<ReferenceLocationItem> page) => new()
    {
        Items = page.Items.Select(i => new ReferenceLocationItemDto
        {
            DeclarationAvailability = i.DeclarationAvailability,
            Origin = i.Origin,
            FilePath = i.FilePath,
            Start = i.Start,
            Length = i.Length,
            ProjectId = i.ProjectId,
            Kind = i.Kind
        }).ToArray(),
        Truncated = page.Truncated,
        NextCursor = page.NextCursor,
        Message = page.Message
    };

    private static SymbolFindImplementationsResultDto ToImplementationsDto(PagedResult<ImplementationItem> page) => new()
    {
        Items = page.Items.Select(i => new ImplementationItemDto
        {
            Handle = i.Handle,
            Summary = ToSummaryDto(i.Summary),
            Locations = i.Locations.Select(l => new SymbolLocationDto
            {
                DeclarationAvailability = l.DeclarationAvailability,
                Origin = l.Origin,
                FilePath = l.FilePath,
                Start = l.Start,
                Length = l.Length
            }).ToArray()
        }).ToArray(),
        Truncated = page.Truncated,
        NextCursor = page.NextCursor,
        Message = page.Message
    };

    private static SymbolTypeHierarchyResultDto ToHierarchyDto(PagedResult<HierarchyItem> page) => new()
    {
        Items = page.Items.Select(i => new HierarchyItemDto
        {
            Kind = i.Kind,
            Handle = i.Handle,
            Summary = ToSummaryDto(i.Summary)
        }).ToArray(),
        Truncated = page.Truncated,
        NextCursor = page.NextCursor,
        Message = page.Message
    };

    private static SymbolFindCallersResultDto ToCallersDto(PagedResult<CallerLocationItem> page) => new()
    {
        Items = page.Items.Select(i => new CallerLocationItemDto
        {
            DeclarationAvailability = i.DeclarationAvailability,
            Origin = i.Origin,
            FilePath = i.FilePath,
            Start = i.Start,
            Length = i.Length,
            ProjectId = i.ProjectId,
            CallerHandle = i.CallerHandle,
            CallerSummary = ToSummaryDto(i.CallerSummary)
        }).ToArray(),
        Truncated = page.Truncated,
        NextCursor = page.NextCursor,
        Message = page.Message
    };

    private static SymbolSummaryDto ToSummaryDto(SymbolSummary summary) => new()
    {
        Kind = summary.Kind,
        DisplayName = summary.DisplayName,
        ContainingSymbol = summary.ContainingSymbol,
        Accessibility = summary.Accessibility,
        ProjectId = summary.ProjectId,
        Language = summary.Language,
        InteropKind = summary.InteropKind
    };
}


