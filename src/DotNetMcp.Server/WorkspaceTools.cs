using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotNetMcp.Server;

[McpServerToolType]
public sealed class WorkspaceTools
{
    private readonly TrustedRoots _trustedRoots;

    public WorkspaceTools(TrustedRoots trustedRoots)
    {
        _trustedRoots = trustedRoots;
    }

    [McpServerTool(Name = "workspace_open"), Description(
        "Validate and accept a .NET solution/project path for a future read-only workspace load (load itself lands in a later release). " +
        "SECURITY: when loading is enabled it will run MSBuild evaluation and project-referenced analyzers/source generators — " +
        "equivalent to executing that repository's build logic. Do not open untrusted codebases. " +
        "All paths must fall under a configured trusted root.")]
    public CallToolResult WorkspaceOpen(
        [Description("Absolute or relative path to a .sln / .slnx / .slnf / project file under a trusted root.")]
        string path)
    {
        if (!_trustedRoots.Contains(path))
        {
            var error = new PolicyErrorDto
            {
                Error = PolicyErrorCodes.PathOutsideTrustedRoots,
                Message = "The requested path is outside the configured trusted roots and was rejected. " +
                          "No target content is returned.",
                SuggestedAction =
                    "Add the directory as a trusted root via --roots or the DOTNET_MCP_TRUSTED_ROOTS " +
                    "environment variable, then retry workspace_open with a path under that root."
            };

            return new CallToolResult
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

        var accepted = new WorkspaceOpenResultDto
        {
            Phase = "accepted",
            Message = "Path is within trusted roots. Full workspace loading is not implemented in this skeleton build.",
            SuggestedAction = "Wait for workspace_open/status implementation (#9); path policy already applies."
        };

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(accepted, JsonOptions.Default)
                }
            ]
        };
    }
}
