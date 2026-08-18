using System.ComponentModel;
using System.Text.Json;
using DotNetMcp.Core;
using DotNetMcp.Xaml;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotNetMcp.Server;

[McpServerToolType]
public sealed class XamlTools
{
    private readonly TrustedRoots _trustedRoots;
    private readonly WorkspaceHost _workspaceHost;
    private readonly XamlDocumentService _xaml;
    private readonly IAuditLogger _audit;

    public XamlTools(
        TrustedRoots trustedRoots,
        WorkspaceHost workspaceHost,
        XamlDocumentService xaml,
        IAuditLogger audit)
    {
        _trustedRoots = trustedRoots;
        _workspaceHost = workspaceHost;
        _xaml = xaml;
        _audit = audit;
    }

    [McpServerTool(Name = "xaml_resolve_class"), Description(
        "Map an Avalonia .axaml document under a trusted root to the x:Class code-behind type SymbolHandle. " +
        "Requires a ready workspace. Other UI frameworks are not registered. " +
        "Missing x:Class, type-not-found, and path-policy failures are distinguishable.")]
    public async Task<CallToolResult> XamlResolveClass(
        [Description("Path to an Avalonia .axaml document under a trusted root.")]
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("xaml_resolve_class", path);

        if (!_trustedRoots.Contains(path))
        {
            _audit.PathPolicyDenied("xaml_resolve_class", path);
            return ErrorResult(new PolicyErrorDto
            {
                Error = PolicyErrorCodes.PathOutsideTrustedRoots,
                Message = "The requested path is outside the configured trusted roots and was rejected.",
                SuggestedAction =
                    "Add the directory as a trusted root via --roots or the DOTNET_MCP_TRUSTED_ROOTS " +
                    "environment variable, then retry xaml_resolve_class with a path under that root."
            });
        }

        if (!TryGetReadySession(out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, xamlError, symbolError) = await _xaml
            .ResolveClassAsync(session!, path, cancellationToken)
            .ConfigureAwait(false);

        if (xamlError is not null)
        {
            return ErrorResult(ToPolicyError(xamlError));
        }

        if (symbolError is not null)
        {
            return ErrorResult(ToPolicyError(symbolError));
        }

        return OkResult(new SymbolResolveResultDto
        {
            Handle = success!.Handle,
            Summary = new SymbolSummaryDto
            {
                Kind = success.Summary.Kind,
                DisplayName = success.Summary.DisplayName,
                ContainingSymbol = success.Summary.ContainingSymbol,
                Accessibility = success.Summary.Accessibility,
                ProjectId = success.Summary.ProjectId,
                Language = success.Summary.Language
            }
        });
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

    private static PolicyErrorDto ToPolicyError(XamlQueryError error) => new()
    {
        Error = error.Code,
        Message = error.Message,
        SuggestedAction = error.SuggestedAction
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
