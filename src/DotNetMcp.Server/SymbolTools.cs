using System.ComponentModel;
using System.Text.Json;
using DotNetMcp.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotNetMcp.Server;

[McpServerToolType]
public sealed class SymbolTools
{
    private readonly WorkspaceHost _workspaceHost;
    private readonly SymbolQueryService _symbols;

    public SymbolTools(WorkspaceHost workspaceHost, SymbolQueryService symbols)
    {
        _workspaceHost = workspaceHost;
        _symbols = symbols;
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

        if (!TryGetReadySession(out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _symbols
            .ResolveByNameAsync(session!.Solution, name, projectId, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return ErrorResult(ToPolicyError(error));
        }

        return OkResult(ToDto(success!));
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

        if (!TryGetReadySession(out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _symbols
            .GetSummaryAsync(session!.Solution, handle, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return ErrorResult(ToPolicyError(error));
        }

        return OkResult(ToDto(success!));
    }

    [McpServerTool(Name = "symbol_goto_definition"), Description(
        "Navigate a SymbolHandle to its definition locations (file/span). Includes handwritten and " +
        "source-generated trees; Origin is Handwritten or SourceGenerated when in source.")]
    public async Task<CallToolResult> SymbolGotoDefinition(
        [Description("SymbolHandle from symbol_resolve: language:projectId:signature#checksum")]
        string handle,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryGetReadySession(out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _symbols
            .GetDefinitionAsync(session!.Solution, handle, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return ErrorResult(ToPolicyError(error));
        }

        return OkResult(ToDefinitionDto(success!));
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

        if (!TryGetReadySession(out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _symbols
            .GetMembersAsync(
                session!.Solution,
                handle,
                session.Epoch,
                limit,
                cursor,
                cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return ErrorResult(ToPolicyError(error));
        }

        return OkResult(ToMembersDto(success!));
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

        if (!TryGetReadySession(out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _symbols
            .FindReferencesAsync(
                session!.Solution,
                handle,
                session.Epoch,
                entireSolution,
                limit,
                cursor,
                softBudget: null,
                cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return ErrorResult(ToPolicyError(error));
        }

        return OkResult(ToFindReferencesDto(success!));
    }

    private bool TryGetReadySession(out IWorkspaceSession? session, out CallToolResult? errorResult)
    {
        if (_workspaceHost.TryGetReadySession(out session) && session is not null)
        {
            errorResult = null;
            return true;
        }

        var status = _workspaceHost.GetStatus();
        errorResult = ErrorResult(new PolicyErrorDto
        {
            Error = PolicyErrorCodes.WorkspaceNotReady,
            Message =
                $"Workspace is not ready (phase={status.Phase}). Query tools cannot run until load completes.",
            SuggestedAction =
                "Call workspace_status to poll until phase is ready; do not retry workspace_open while loading."
        });
        return false;
    }

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

    private static SymbolSummaryDto ToSummaryDto(SymbolSummary summary) => new()
    {
        Kind = summary.Kind,
        DisplayName = summary.DisplayName,
        ContainingSymbol = summary.ContainingSymbol,
        Accessibility = summary.Accessibility,
        ProjectId = summary.ProjectId,
        Language = summary.Language
    };

    private static PolicyErrorDto ToPolicyError(SymbolQueryError error) => new()
    {
        Error = error.Code,
        Message = error.Message,
        SuggestedAction = error.SuggestedAction
    };

    private static CallToolResult OkResult<T>(T payload) => new()
    {
        Content =
        [
            new TextContentBlock
            {
                Text = JsonSerializer.Serialize(payload, JsonOptions.Default)
            }
        ]
    };

    private static CallToolResult ErrorResult(PolicyErrorDto error) => new()
    {
        IsError = true,
        Content =
        [
            new TextContentBlock
            {
                Text = JsonSerializer.Serialize(error, JsonOptions.Default)
            }
        ]
    };
}
