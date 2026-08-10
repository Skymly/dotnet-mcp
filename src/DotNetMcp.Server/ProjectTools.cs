using System.ComponentModel;
using System.Text.Json;
using DotNetMcp.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotNetMcp.Server;

[McpServerToolType]
public sealed class ProjectTools
{
    private readonly WorkspaceHost _workspaceHost;
    private readonly DiagnosticQueryService _diagnostics;

    public ProjectTools(WorkspaceHost workspaceHost, DiagnosticQueryService diagnostics)
    {
        _workspaceHost = workspaceHost;
        _diagnostics = diagnostics;
    }

    [McpServerTool(Name = "project_diagnostics"), Description(
        "List compile errors and warnings for a projectId with forced pagination. " +
        "Fails with WorkspaceNotReady when the workspace is still loading — call workspace_status instead. " +
        "Cursors bind to the workspace epoch.")]
    public async Task<CallToolResult> ProjectDiagnostics(
        [Description("Roslyn projectId GUID string from workspace_list_projects.")]
        string projectId,
        [Description("Page size (default 50, max 100).")]
        int? limit = null,
        [Description("Opaque nextCursor from a previous project_diagnostics page.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryGetReadySession(out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _diagnostics
            .GetProjectDiagnosticsAsync(
                session!.Solution,
                projectId,
                session.Epoch,
                limit,
                cursor,
                softBudget: null,
                cancellationToken)
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

    private static ProjectDiagnosticsResultDto ToDto(PagedResult<DiagnosticItem> page) => new()
    {
        Items = page.Items.Select(d => new DiagnosticItemDto
        {
            Id = d.Id,
            Severity = d.Severity,
            Message = d.Message,
            FilePath = d.FilePath,
            StartLine = d.StartLine,
            StartCharacter = d.StartCharacter,
            EndLine = d.EndLine,
            EndCharacter = d.EndCharacter,
            ProjectId = d.ProjectId
        }).ToArray(),
        Truncated = page.Truncated,
        NextCursor = page.NextCursor,
        Message = page.Message
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
