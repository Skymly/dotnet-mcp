using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotNetMcp.Server;

[McpServerToolType]
public sealed class WorkspaceTools
{
    private readonly TrustedRoots _trustedRoots;
    private readonly WorkspaceHost _workspaceHost;
    private readonly IAuditLogger _audit;

    public WorkspaceTools(TrustedRoots trustedRoots, WorkspaceHost workspaceHost, IAuditLogger audit)
    {
        _trustedRoots = trustedRoots;
        _workspaceHost = workspaceHost;
        _audit = audit;
    }

    [McpServerTool(Name = "workspace_open"), Description(
        "Start loading a .NET solution/project into the single active workspace and return immediately. " +
        "For large repos prefer a .slnf or a single project file over a 150+ project solution; load does not compile all projects. Poll workspace_status until phase is ready (do not retry this tool while loading). " +
        "SECURITY: loading runs MSBuild evaluation and project-referenced analyzers/source generators — " +
        "equivalent to executing that repository's build logic. Do not open untrusted codebases. " +
        "All paths must fall under a configured trusted root.")]
    public CallToolResult WorkspaceOpen(
        [Description("Absolute or relative path to a .sln / .slnx / .slnf / project file under a trusted root.")]
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("workspace_open", path);

        if (!_trustedRoots.Contains(path))
        {
            _audit.PathPolicyDenied("workspace_open", path);
            return McpToolEnvelope.ErrorResult(new PolicyErrorDto
            {
                Error = PolicyErrorCodes.PathOutsideTrustedRoots,
                Message = "The requested path is outside the configured trusted roots and was rejected. " +
                          "No target content is returned.",
                SuggestedAction =
                    "Add the directory as a trusted root via --roots or the DOTNET_MCP_TRUSTED_ROOTS " +
                    "environment variable, then retry workspace_open with a path under that root."
            });
        }

        string fullPath;
        try
        {
            fullPath = PathPolicy.Normalize(path);
        }
        catch (Exception)
        {
            return McpToolEnvelope.ErrorResult(new PolicyErrorDto
            {
                Error = PolicyErrorCodes.InvalidWorkspacePath,
                Message = "The path could not be resolved.",
                SuggestedAction = "Pass a valid filesystem path under a trusted root, then call workspace_open again."
            });
        }

        // Re-check the canonical path (same string BeginOpen will use).
        if (!_trustedRoots.ContainsNormalized(fullPath))
        {
            _audit.PathPolicyDenied("workspace_open", path);
            return McpToolEnvelope.ErrorResult(new PolicyErrorDto
            {
                Error = PolicyErrorCodes.PathOutsideTrustedRoots,
                Message = "The requested path is outside the configured trusted roots and was rejected. " +
                          "No target content is returned.",
                SuggestedAction =
                    "Add the directory as a trusted root via --roots or the DOTNET_MCP_TRUSTED_ROOTS " +
                    "environment variable, then retry workspace_open with a path under that root."
            });
        }

        if (!File.Exists(fullPath))
        {
            return McpToolEnvelope.ErrorResult(new PolicyErrorDto
            {
                Error = PolicyErrorCodes.InvalidWorkspacePath,
                Message = "The workspace path does not exist or is not a file.",
                SuggestedAction =
                    "Provide an existing .sln / .slnx / .slnf (or project) file under a trusted root, " +
                    "then call workspace_open again."
            });
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Background load is intentionally NOT tied to this request token (ADR-0003 §1 non-blocking).
        var status = _workspaceHost.BeginOpen(fullPath);
        var open = WorkspaceOpenResultDto.FromStatus(status);
        return McpToolEnvelope.OkResult(open);
    }

    [McpServerTool(Name = "workspace_status"), Description(
        "Poll the active workspace load. Returns phase, progress counters, warnings, and SuggestedAction. " +
        "While loading, keep polling this tool — do not retry workspace_open.")]
    public CallToolResult WorkspaceStatus(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("workspace_status");
        return McpToolEnvelope.OkResult(_workspaceHost.GetStatus());
    }

    [McpServerTool(Name = "workspace_list_projects"), Description(
        "List projects in the ready workspace as one row per ProjectId (multi-TFM appears as separate rows). " +
        "Fails with WorkspaceNotReady when the workspace is still loading — call workspace_status instead.")]
    public CallToolResult WorkspaceListProjects(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("workspace_list_projects");

        if (!McpToolEnvelope.TryGetReadySession(_workspaceHost, out var session, out var notReady))
        {
            return notReady!;
        }

        var result = new WorkspaceListProjectsResultDto
        {
            Projects = ProjectSummary.FromSolution(session.Solution)
        };
        return McpToolEnvelope.OkResult(result);
    }

    [McpServerTool(Name = "workspace_check_drift"), Description(
        "Compare tracked workspace documents to on-disk content (fallback when FileSystemWatcher misses a change). " +
        "Also detects project/solution file mtime changes. Repairs source-file content mismatches and advances the " +
        "workspace epoch; project/solution drifts require workspace_open. Fails with WorkspaceNotReady while loading.")]
    public CallToolResult WorkspaceCheckDrift(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("workspace_check_drift");

        if (!McpToolEnvelope.TryGetReadySession(_workspaceHost, out _, out var notReady))
        {
            return notReady!;
        }

        return McpToolEnvelope.OkResult(_workspaceHost.CheckDrift());
    }
}


