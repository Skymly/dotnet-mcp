using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using DotNetMcp.Core;
using DotNetMcp.Xaml;
using ModelContextProtocol.Protocol;

namespace DotNetMcp.Server;

public static class McpToolEnvelope
{
    public static bool TryGetReadySession(
        WorkspaceHost host,
        [NotNullWhen(true)] out IWorkspaceSession? session,
        [NotNullWhen(false)] out CallToolResult? errorResult)
    {
        if (host.TryGetReadySession(out session) && session is not null)
        {
            errorResult = null;
            return true;
        }

        var status = host.GetStatus();
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

    public static PolicyErrorDto ToPolicyError(SymbolQueryError error) => new()
    {
        Error = error.Code,
        Message = error.Message,
        SuggestedAction = error.SuggestedAction
    };

    public static PolicyErrorDto ToPolicyError(XamlQueryError error) => new()
    {
        Error = error.Code,
        Message = error.Message,
        SuggestedAction = error.SuggestedAction
    };

    public static CallToolResult OkResult<T>(T payload) => new()
    {
        Content =
        [
            new TextContentBlock
            {
                Text = JsonSerializer.Serialize(payload, JsonOptions.Default)
            }
        ]
    };

    public static CallToolResult ErrorResult(PolicyErrorDto error) => new()
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
