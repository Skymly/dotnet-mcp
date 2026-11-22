using System.ComponentModel;
using DotNetMcp.FSharp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotNetMcp.Server;

[McpServerToolType]
public sealed class FSharpTools(WorkspaceHost host, FSharpSymbolQueryService fsharp, IAuditLogger audit)
{
    [McpServerTool(Name = "fsharp_signature_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Compare an F# implementation .fs with the public .fsi in its project's compile inputs. " +
        "Returns located implementation-only declarations for a conforming pair, or FCS compiler errors for the pair.")]
    public async Task<CallToolResult> SignatureStatus(
        [Description("F# projectId from workspace_list_projects.")] string projectId,
        [Description("Absolute path of a .fs file in this project.")] string implementationFile,
        CancellationToken cancellationToken = default)
    {
        audit.ToolInvoked("fsharp_signature_status");
        if (!McpToolEnvelope.TryGetReadySession(host, out var session, out var notReady))
        {
            return notReady!;
        }

        var (status, error) = await fsharp.SignatureStatusAsync(session!.FSharpSnapshot, projectId, implementationFile, cancellationToken)
            .ConfigureAwait(false);
        return error is null ? McpToolEnvelope.OkResult(status!) : McpToolEnvelope.ErrorResult(McpToolEnvelope.ToPolicyError(error));
    }
}
