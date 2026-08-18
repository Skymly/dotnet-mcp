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

    [McpServerTool(Name = "xaml_list_xmlns"), Description(
        "List xmlns prefix mappings for an Avalonia .axaml document under a trusted root: " +
        "using:, clr-namespace:, and XmlnsDefinitionAttribute on referenced assemblies. " +
        "Optional prefix filters; unknown prefix and missing document are distinguishable.")]
    public async Task<CallToolResult> XamlListXmlns(
        [Description("Path to an Avalonia .axaml document under a trusted root.")]
        string path,
        [Description("Optional xmlns prefix to resolve (empty string is the default xmlns).")]
        string? prefix = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("xaml_list_xmlns", path);

        if (!_trustedRoots.Contains(path))
        {
            _audit.PathPolicyDenied("xaml_list_xmlns", path);
            return ErrorResult(new PolicyErrorDto
            {
                Error = PolicyErrorCodes.PathOutsideTrustedRoots,
                Message = "The requested path is outside the configured trusted roots and was rejected.",
                SuggestedAction =
                    "Add the directory as a trusted root via --roots or the DOTNET_MCP_TRUSTED_ROOTS " +
                    "environment variable, then retry xaml_list_xmlns with a path under that root."
            });
        }

        if (!TryGetReadySession(out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _xaml
            .ListXmlnsAsync(session!, path, prefix, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return ErrorResult(ToPolicyError(error));
        }

        return OkResult(new XamlListXmlnsResultDto
        {
            Items = success!.Select(m => new XamlXmlnsMappingDto
            {
                Prefix = m.Prefix,
                XmlNamespace = m.XmlNamespace,
                ClrNamespace = m.ClrNamespace,
                AssemblyName = m.AssemblyName,
                Source = m.Source
            }).ToArray()
        });
    }

    [McpServerTool(Name = "xaml_resolve_name"), Description(
        "Map an x:Name in an Avalonia .axaml document to the NameGenerator field SymbolHandle on the x:Class type. " +
        "Missing x:Name vs NameGenerator-not-run are distinguishable. Use symbol_attribution on the handle.")]
    public async Task<CallToolResult> XamlResolveName(
        [Description("Path to an Avalonia .axaml document under a trusted root.")]
        string path,
        [Description("x:Name value declared in the document.")]
        string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("xaml_resolve_name", path);

        if (!_trustedRoots.Contains(path))
        {
            _audit.PathPolicyDenied("xaml_resolve_name", path);
            return ErrorResult(new PolicyErrorDto
            {
                Error = PolicyErrorCodes.PathOutsideTrustedRoots,
                Message = "The requested path is outside the configured trusted roots and was rejected.",
                SuggestedAction =
                    "Add the directory as a trusted root via --roots or the DOTNET_MCP_TRUSTED_ROOTS " +
                    "environment variable, then retry xaml_resolve_name with a path under that root."
            });
        }

        if (!TryGetReadySession(out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, xamlError, symbolError) = await _xaml
            .ResolveNameAsync(session!, path, name, cancellationToken)
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

    [McpServerTool(Name = "xaml_resolve_binding"), Description(
        "Resolve a Binding Path under x:DataType / CompiledBindings to each segment's property SymbolHandle. " +
        "Walks types in-process (no MCP DTO N+1). Missing property and type mismatch are distinguishable. " +
        "Code-behind-only DataContext is out of scope.")]
    public async Task<CallToolResult> XamlResolveBinding(
        [Description("Path to an Avalonia .axaml document under a trusted root.")]
        string path,
        [Description("Binding Path, e.g. Name or Home.City.")]
        string bindingPath,
        [Description("Optional x:DataType (prefix:Name or CLR name). Defaults to the first x:DataType in the document.")]
        string? dataType = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("xaml_resolve_binding", path);

        if (!_trustedRoots.Contains(path))
        {
            _audit.PathPolicyDenied("xaml_resolve_binding", path);
            return ErrorResult(new PolicyErrorDto
            {
                Error = PolicyErrorCodes.PathOutsideTrustedRoots,
                Message = "The requested path is outside the configured trusted roots and was rejected.",
                SuggestedAction =
                    "Add the directory as a trusted root via --roots or the DOTNET_MCP_TRUSTED_ROOTS " +
                    "environment variable, then retry xaml_resolve_binding with a path under that root."
            });
        }

        if (!TryGetReadySession(out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, xamlError, symbolError) = await _xaml
            .ResolveBindingAsync(session!, path, bindingPath, dataType, cancellationToken)
            .ConfigureAwait(false);
        if (xamlError is not null)
        {
            return ErrorResult(ToPolicyError(xamlError));
        }

        if (symbolError is not null)
        {
            return ErrorResult(ToPolicyError(symbolError));
        }

        return OkResult(new XamlResolveBindingResultDto
        {
            Items = success!.Select(s => new XamlBindingSegmentDto
            {
                Name = s.Name,
                Handle = s.Handle,
                Summary = new SymbolSummaryDto
                {
                    Kind = s.Summary.Kind,
                    DisplayName = s.Summary.DisplayName,
                    ContainingSymbol = s.Summary.ContainingSymbol,
                    Accessibility = s.Summary.Accessibility,
                    ProjectId = s.Summary.ProjectId,
                    Language = s.Summary.Language
                }
            }).ToArray()
        });
    }

    [McpServerTool(Name = "xaml_diagnostics"), Description(
        "Semantic Avalonia XAML diagnostics (unknown elements/properties given xmlns, bad Binding paths, unmatched x:Name). " +
        "Not XML well-formedness. Paged with a soft budget; stale cursors fail distinctly.")]
    public async Task<CallToolResult> XamlDiagnostics(
        [Description("Path to an Avalonia .axaml document under a trusted root.")]
        string path,
        [Description("Page size (default 50, max 100).")]
        int? limit = null,
        [Description("Opaque nextCursor from a previous xaml_diagnostics page.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("xaml_diagnostics", path);

        if (!_trustedRoots.Contains(path))
        {
            _audit.PathPolicyDenied("xaml_diagnostics", path);
            return ErrorResult(new PolicyErrorDto
            {
                Error = PolicyErrorCodes.PathOutsideTrustedRoots,
                Message = "The requested path is outside the configured trusted roots and was rejected.",
                SuggestedAction =
                    "Add the directory as a trusted root via --roots or the DOTNET_MCP_TRUSTED_ROOTS " +
                    "environment variable, then retry xaml_diagnostics with a path under that root."
            });
        }

        if (!TryGetReadySession(out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, xamlError, symbolError) = await _xaml
            .GetDiagnosticsAsync(session!, path, limit, cursor, softBudget: null, cancellationToken)
            .ConfigureAwait(false);
        if (xamlError is not null)
        {
            return ErrorResult(ToPolicyError(xamlError));
        }

        if (symbolError is not null)
        {
            return ErrorResult(ToPolicyError(symbolError));
        }

        return OkResult(new ProjectDiagnosticsResultDto
        {
            Items = success!.Items.Select(i => new DiagnosticItemDto
            {
                Id = i.Id,
                Severity = i.Severity,
                Message = i.Message,
                FilePath = i.FilePath,
                StartLine = i.StartLine,
                StartCharacter = i.StartCharacter,
                EndLine = i.EndLine,
                EndCharacter = i.EndCharacter,
                ProjectId = i.ProjectId
            }).ToArray(),
            Truncated = success.Truncated,
            NextCursor = success.NextCursor,
            Message = success.Message
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
