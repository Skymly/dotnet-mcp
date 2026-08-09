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
        Summary = new SymbolSummaryDto
        {
            Kind = success.Summary.Kind,
            DisplayName = success.Summary.DisplayName,
            ContainingSymbol = success.Summary.ContainingSymbol,
            Accessibility = success.Summary.Accessibility,
            ProjectId = success.Summary.ProjectId,
            Language = success.Summary.Language
        }
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
