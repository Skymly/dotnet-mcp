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
        if (status.ErrorCode == PolicyErrorCodes.LoadedGraphOutsideTrustedRoots)
        {
            errorResult = ErrorResult(new PolicyErrorDto
            {
                Error = PolicyErrorCodes.LoadedGraphOutsideTrustedRoots,
                Message = status.Error ??
                          "The loaded graph has a project or document outside trusted roots.",
                SuggestedAction = status.SuggestedAction
            });
            return false;
        }

        var message = status.Phase switch
        {
            "failed" or "cancelled" =>
                $"Workspace is not ready (phase={status.Phase}). Query tools cannot run; load is not in progress.",
            "idle" =>
                "Workspace is not ready (phase=idle). Open a workspace before calling query tools.",
            _ =>
                $"Workspace is not ready (phase={status.Phase}). Query tools cannot run until load completes.",
        };

        errorResult = ErrorResult(new PolicyErrorDto
        {
            Error = PolicyErrorCodes.WorkspaceNotReady,
            Message = message,
            SuggestedAction = status.SuggestedAction
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

    public static bool TryRejectBlankPath(
        string? path,
        string toolName,
        bool xaml,
        [NotNullWhen(true)] out CallToolResult? errorResult)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            errorResult = null;
            return false;
        }

        errorResult = ErrorResult(xaml
            ? new PolicyErrorDto
            {
                Error = PolicyErrorCodes.XamlDocumentNotFound,
                Message = "XAML document path is empty.",
                SuggestedAction = "Pass the path of an Avalonia .axaml or MAUI .xaml document, then retry " + toolName + "."
            }
            : new PolicyErrorDto
            {
                Error = PolicyErrorCodes.InvalidWorkspacePath,
                Message = "The workspace path is empty.",
                SuggestedAction = "Pass a non-empty path to an existing .sln, .slnx, .slnf, or project file, then retry " + toolName + "."
            });
        return true;
    }

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
