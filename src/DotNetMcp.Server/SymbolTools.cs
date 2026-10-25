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
    private readonly LanguageAdapters _languages;
    private readonly TrustedRoots _trustedRoots;
    private readonly IAuditLogger _audit;

    public SymbolTools(
        WorkspaceHost workspaceHost,
        WorkspaceEdit workspaceEdit,
        LanguageAdapters languages,
        TrustedRoots trustedRoots,
        IAuditLogger audit)
    {
        _workspaceHost = workspaceHost;
        _workspaceEdit = workspaceEdit;
        _languages = languages;
        _trustedRoots = trustedRoots;
        _audit = audit;
    }

    [McpServerTool(Name = "symbol_resolve", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Resolve a C# / VB / F# symbol by name or FQN in the ready workspace and return a verifiable SymbolHandle " +
        "plus a lightweight summary (no member tree). Optional projectId from workspace_list_projects disambiguates multi-TFM / multi-project hits.")]
    public async Task<CallToolResult> SymbolResolve(
        [Description("Type or member name / FQN (e.g. SampleLib.Calculator).")]
        string name,
        [Description("Optional projectId GUID string from workspace_list_projects.")]
        string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("symbol_resolve");

        if (!McpToolEnvelope.TryGetReadySession(_workspaceHost, out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _languages
            .ResolveByNameAsync(session!, name, projectId, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return McpToolEnvelope.ErrorResult(McpToolEnvelope.ToPolicyError(error));
        }

        return McpToolEnvelope.OkResult(ToDto(success!));
    }

    [McpServerTool(Name = "symbol_resolve_at", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Resolve the symbol at a source position to a verifiable SymbolHandle plus summary — the position entry of " +
        "the same addressable set symbol_resolve returns (ADR-0001 Amendment 7). Positions use the unified model " +
        "(1-based line/column, UTF-16 code units, tab = one column, endColumn exclusive): filePath + line + column " +
        "emitted by project_diagnostics, symbol_find_references, symbol_find_callers, symbol_goto_definition or a " +
        "grep-style hit feed this tool verbatim. Returns the matched identifier span/text and resolvedFrom " +
        "(Declaration or Reference); when overload resolution fails but a single candidate remains, candidateReason " +
        "says why. Optional projectId disambiguates linked / multi-TFM documents. .axaml/.xaml documents return " +
        "UnsupportedDocumentForPosition — a XAML position entry arrives with a later XAML ticket (use xaml_* tools). " +
        "Does not open buffers and never writes disk.")]
    public async Task<CallToolResult> SymbolResolveAt(
        [Description("Source file path under a trusted root (regular or source-generated document).")]
        string path,
        [Description("1-based line number.")]
        int line,
        [Description("1-based column in UTF-16 code units; a column past the line end clamps to the line end.")]
        int column,
        [Description("Optional projectId GUID string from workspace_list_projects.")]
        string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("symbol_resolve_at", path);

        if (McpToolEnvelope.TryRejectBlankPath(path, "symbol_resolve_at", xaml: false, out var blankPath))
        {
            return blankPath;
        }

        // Document paths stored in the workspace/snapshots are canonical, so the
        // input must be canonicalized the same way TrustedRoots does — otherwise a
        // path through a symlinked ancestor (macOS /var -> /private/var) misses
        // every stored document. Unresolvable paths fail closed as outside roots.
        string canonicalPath;
        try
        {
            canonicalPath = PathPolicy.Normalize(path);
        }
        catch (Exception ex) when (ex is PathPolicyException or ArgumentException)
        {
            _audit.PathPolicyDenied("symbol_resolve_at", path);
            return McpToolEnvelope.ErrorResult(McpToolEnvelope.PathOutsideTrustedRoots());
        }

        if (!_trustedRoots.ContainsNormalized(canonicalPath))
        {
            _audit.PathPolicyDenied("symbol_resolve_at", path);
            return McpToolEnvelope.ErrorResult(McpToolEnvelope.PathOutsideTrustedRoots());
        }

        if (!McpToolEnvelope.TryGetReadySession(_workspaceHost, out var session, out var notReady))
        {
            return notReady!;
        }

        if (path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            return McpToolEnvelope.ErrorResult(McpToolEnvelope.ToPolicyError(
                new UnsupportedDocumentForPositionError(
                    "XAML documents do not have a position-based symbol surface yet; a XAML position entry arrives with a later XAML ticket.",
                    "Use the xaml_* tools (xaml_resolve_class, xaml_list_xmlns, xaml_resolve_name, xaml_resolve_binding, xaml_diagnostics) for XAML documents.")));
        }

        var (success, error) = await _languages
            .ResolveAtPositionAsync(session!, canonicalPath, line, column, projectId, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return McpToolEnvelope.ErrorResult(McpToolEnvelope.ToPolicyError(error));
        }

        return McpToolEnvelope.OkResult(ToResolveAtDto(success!));
    }

    [McpServerTool(Name = "symbol_summary", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
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

        var (success, error) = await _languages
            .GetSummaryAsync(session!, handle, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return McpToolEnvelope.ErrorResult(McpToolEnvelope.ToPolicyError(error));
        }

        return McpToolEnvelope.OkResult(ToDto(success!));
    }

    [McpServerTool(Name = "symbol_goto_definition", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
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

        var (success, error) = await _languages
            .GetDefinitionAsync(session!, handle, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return McpToolEnvelope.ErrorResult(McpToolEnvelope.ToPolicyError(error));
        }

        return McpToolEnvelope.OkResult(ToDefinitionDto(success!));
    }

    [McpServerTool(Name = "symbol_attribution", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Two-axis symbol attribution for a C# / VB SymbolHandle: declaration availability plus Handwritten vs " +
        "SourceGenerator(identity) via public GeneratorDriver reconciliation (not FilePath heuristics). " +
        "Named types also return a members map keyed by signature-qualified name (partial/overload safe). " +
        "F# handles return GeneratorLanguageNotSupported.")]
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

        var (success, error) = await _languages
            .GetAttributionAsync(session!, handle, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return McpToolEnvelope.ErrorResult(McpToolEnvelope.ToPolicyError(error));
        }

        return McpToolEnvelope.OkResult(ToAttributionDto(success!));
    }

    [McpServerTool(Name = "symbol_source", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Return verbatim declaration source text for a C# / VB / F# SymbolHandle — cheaper than reading the whole " +
        "file because it slices only the declaration (XML doc, attributes, members of a type). Source-generated " +
        "code never lands on disk, so host file tools cannot read it: this is the only way to read generated " +
        "source. Handwritten declarations come first (ordered by file/position), generated ones last, each with " +
        "Origin and generator identity. Positions are 1-based UTF-16 code units, endColumn exclusive. " +
        "maxChars (default 12000, max 60000) is a text budget spent declaration-by-declaration at line " +
        "boundaries; a declaration over budget keeps only whole lines and later declarations list with empty " +
        "text and truncated=true, so a truncated declaration's end position marks where the text stopped. " +
        "Truncated types hint to page members via symbol_members first. Metadata-only symbols fail with " +
        "SourceUnavailable — no decompilation.")]
    public async Task<CallToolResult> SymbolSource(
        [Description("SymbolHandle from symbol_resolve: language:projectId:signature#checksum")]
        string handle,
        [Description("Text budget for declaration text (default 12000, max 60000; non-positive resets to default).")]
        int? maxChars = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("symbol_source");

        if (!McpToolEnvelope.TryGetReadySession(_workspaceHost, out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _languages
            .GetSourceAsync(session!, handle, maxChars, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return McpToolEnvelope.ErrorResult(McpToolEnvelope.ToPolicyError(error));
        }

        return McpToolEnvelope.OkResult(ToSourceDto(success!));
    }

    [McpServerTool(Name = "symbol_members", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
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

        var (success, error) = await _languages
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

    [McpServerTool(Name = "symbol_find_references", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Find references to a SymbolHandle. Default scope is the defining project plus projects that depend on it; " +
        "pass entireSolution=true to search the whole solution. Soft time budget may truncate with nextCursor " +
        "(do not restart from scratch). Cursors bind to the workspace epoch. A source-generated location that " +
        "cannot be reconciled fails the page with CompilationUnavailable instead of being reported as Handwritten. " +
        "F# handles search only the defining project; entireSolution does not widen F# search, and the message says so.")]
    public async Task<CallToolResult> SymbolFindReferences(
        [Description("SymbolHandle from symbol_resolve: language:projectId:signature#checksum")]
        string handle,
        [Description("When true, search the entire solution; default false uses the defining project and its dependents.")]
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

        var (success, error) = await _languages
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

    [McpServerTool(Name = "symbol_find_implementations", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Find types and members that implement or derive from a SymbolHandle (interfaces, abstract/virtual " +
        "members, and class inheritance). Results are paginated; cursors bind to the workspace epoch. " +
        "F# handles search only the defining project.")]
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

        var (success, error) = await _languages
            .FindImplementationsAsync(session!, handle, limit, cursor, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return McpToolEnvelope.ErrorResult(McpToolEnvelope.ToPolicyError(error));
        }

        return McpToolEnvelope.OkResult(ToImplementationsDto(success!));
    }

    [McpServerTool(Name = "symbol_type_hierarchy", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Return a type SymbolHandle's base-type chain (immediate to root) then implemented interfaces, " +
        "paginated. Cursors bind to the workspace epoch and become stale when the workspace generation advances. " +
        "For F# handles the chain covers only types in the same project and stops at the first external base type, " +
        "which the message names.")]
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

        var (success, error) = await _languages
            .GetTypeHierarchyAsync(session!, handle, limit, cursor, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return McpToolEnvelope.ErrorResult(McpToolEnvelope.ToPolicyError(error));
        }

        return McpToolEnvelope.OkResult(ToHierarchyDto(success!));
    }

    [McpServerTool(Name = "symbol_find_callers", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Find direct call sites of a method SymbolHandle (shallow callers, not a full call graph). " +
        "Default scope is the defining project plus projects that depend on it; pass entireSolution=true to search the whole solution. " +
        "Soft time budget may truncate with nextCursor. Cursors bind to the workspace epoch. A source-generated " +
        "caller location that cannot be reconciled fails the page with CompilationUnavailable instead of being reported as Handwritten. " +
        "F# handles search only the defining project; entireSolution does not widen F# search, and the message says so.")]
    public async Task<CallToolResult> SymbolFindCallers(
        [Description("Method SymbolHandle from symbol_resolve.")]
        string handle,
        [Description("When true, search the entire solution; default false uses the defining project and its dependents.")]
        bool entireSolution = false,
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

        var (success, error) = await _languages
            .FindCallersAsync(session!, handle, entireSolution, limit, cursor, softBudget: null, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return McpToolEnvelope.ErrorResult(McpToolEnvelope.ToPolicyError(error));
        }

        return McpToolEnvelope.OkResult(ToCallersDto(success!));
    }

    [McpServerTool(Name = "symbol_preview_rename", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Preview renaming a handwritten C# / VB / F# SymbolHandle. Returns a Workspace Edit (per-file old/new text, " +
        "handles that will become invalid) and an opaque previewId bound to the current workspace Epoch + TTL. " +
        "Does not write disk. SourceGenerator Origin is refused. An F# symbol referenced by other projects " +
        "in the workspace is refused with FSharpCrossProjectRenameUnsupported. " +
        "There is no generic apply_edit / write / shell.")]
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

        var (draft, error) = await _languages
            .BuildRenamePreviewAsync(session!, handle, newName, cancellationToken)
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

    [McpServerTool(Name = "symbol_apply_rename", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Apply a still-valid C# / VB / F# rename preview. Writes only the documents listed in that preview, all of which " +
        "must already exist inside a trusted root. Uses WriteSuppression and advances the workspace Epoch. " +
        "There is no apply path that skips preview. Refuses with SourceEncodingRefused, and writes nothing, when a listed document has no BOM and is not valid UTF-8. Not a generic write / patch / shell tool.")]
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

    private static SymbolResolveAtResultDto ToResolveAtDto(SymbolResolveAtSuccess success) => new()
    {
        Handle = success.Handle,
        Summary = ToSummaryDto(success.Summary),
        Matched = new MatchedIdentifierDto
        {
            Line = success.Matched.Line,
            Column = success.Matched.Column,
            EndLine = success.Matched.EndLine,
            EndColumn = success.Matched.EndColumn,
            Text = success.Matched.Text
        },
        ResolvedFrom = success.ResolvedFrom,
        CandidateReason = success.CandidateReason
    };

    private static SymbolDefinitionResultDto ToDefinitionDto(SymbolDefinitionSuccess success) => new()
    {
        Locations = success.Locations.Select(ToLocationDto).ToArray()
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

    private static SymbolSourceResultDto ToSourceDto(SymbolSourceSuccess success) => new()
    {
        Declarations = success.Declarations.Select(d => new SymbolSourceDeclarationDto
        {
            Origin = d.Origin,
            Generator = d.Generator is null
                ? null
                : new GeneratorIdentityDto
                {
                    AssemblyName = d.Generator.AssemblyName,
                    TypeFullName = d.Generator.TypeFullName,
                    Version = d.Generator.Version
                },
            FilePath = d.FilePath,
            Line = d.Line,
            Column = d.Column,
            EndLine = d.EndLine,
            EndColumn = d.EndColumn,
            Text = d.Text,
            Truncated = d.Truncated
        }).ToArray(),
        Truncated = success.Truncated,
        Message = success.Message
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
            Line = i.Line,
            Column = i.Column,
            EndLine = i.EndLine,
            EndColumn = i.EndColumn,
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
            Locations = i.Locations.Select(ToLocationDto).ToArray()
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
            Line = i.Line,
            Column = i.Column,
            EndLine = i.EndLine,
            EndColumn = i.EndColumn,
            ProjectId = i.ProjectId,
            CallerHandle = i.CallerHandle,
            CallerSummary = ToSummaryDto(i.CallerSummary)
        }).ToArray(),
        Truncated = page.Truncated,
        NextCursor = page.NextCursor,
        Message = page.Message
    };

    private static SymbolLocationDto ToLocationDto(SymbolLocation l) => new()
    {
        DeclarationAvailability = l.DeclarationAvailability,
        Origin = l.Origin,
        FilePath = l.FilePath,
        Start = l.Start,
        Length = l.Length,
        Line = l.Line,
        Column = l.Column,
        EndLine = l.EndLine,
        EndColumn = l.EndColumn
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


