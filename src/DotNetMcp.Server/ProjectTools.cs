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
    private readonly GeneratorQueryService _generators;
    private readonly DynamicInvocationQueryService _dynamicInvocations;
    private readonly IAuditLogger _audit;

    public ProjectTools(
        WorkspaceHost workspaceHost,
        DiagnosticQueryService diagnostics,
        GeneratorQueryService generators,
        DynamicInvocationQueryService dynamicInvocations,
        IAuditLogger audit)
    {
        _workspaceHost = workspaceHost;
        _diagnostics = diagnostics;
        _generators = generators;
        _dynamicInvocations = dynamicInvocations;
        _audit = audit;
    }

    [McpServerTool(Name = "project_diagnostics"), Description(
        "List compile errors and warnings for a projectId with forced pagination. " +
        "Soft time budget may truncate with nextCursor (do not restart from scratch). " +
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
        _audit.ToolInvoked("project_diagnostics");

        if (!TryGetReadySession(out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _diagnostics
            .GetProjectDiagnosticsAsync(
                session!,
                projectId,
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

    [McpServerTool(Name = "project_list_generators"), Description(
        "List source generators registered on a project (assembly name, type full name, version) " +
        "via AnalyzerReferences.GetGenerators — not FilePath heuristics. " +
        "Fails with WorkspaceNotReady when the workspace is still loading — call workspace_status instead. " +
        "Results are cached per (projectId, workspace epoch).")]
    public async Task<CallToolResult> ProjectListGenerators(
        [Description("Roslyn projectId GUID string from workspace_list_projects.")]
        string projectId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("project_list_generators");

        if (!TryGetReadySession(out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _generators
            .ListGeneratorsAsync(
                session!,
                projectId,
                cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return ErrorResult(ToPolicyError(error));
        }

        return OkResult(new ProjectListGeneratorsResultDto
        {
            Generators = success!.Select(g => new GeneratorIdentityDto
            {
                AssemblyName = g.AssemblyName,
                TypeFullName = g.TypeFullName,
                Version = g.Version
            }).ToArray(),
            Epoch = session.Epoch
        });
    }

    [McpServerTool(Name = "project_list_generated_sources"), Description(
        "List GeneratedSources for one source generator identity (HintName + content) with forced pagination. " +
        "HintName is not assumed unique across generators — filter by assemblyName + typeFullName. " +
        "Uses public GeneratorDriver reconciliation (ADR-0001 §6). Cursors bind to workspace epoch.")]
    public async Task<CallToolResult> ProjectListGeneratedSources(
        [Description("Roslyn projectId GUID string from workspace_list_projects.")]
        string projectId,
        [Description("Generator assembly name from project_list_generators.")]
        string assemblyName,
        [Description("Generator type full name from project_list_generators.")]
        string typeFullName,
        [Description("Page size (default 50, max 100).")]
        int? limit = null,
        [Description("Opaque nextCursor from a previous project_list_generated_sources page.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("project_list_generated_sources");

        if (!TryGetReadySession(out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _generators
            .ListGeneratedSourcesAsync(
                session!,
                projectId,
                assemblyName,
                typeFullName,
                limit,
                cursor,
                cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return ErrorResult(ToPolicyError(error));
        }

        return OkResult(new ProjectListGeneratedSourcesResultDto
        {
            Items = success!.Items.Select(i => new GeneratedSourceItemDto
            {
                HintName = i.HintName,
                Content = i.Content
            }).ToArray(),
            Truncated = success.Truncated,
            NextCursor = success.NextCursor,
            Message = success.Message,
            Epoch = session.Epoch
        });
    }

    [McpServerTool(Name = "project_list_generator_diagnostics"), Description(
        "List diagnostics reported by one source generator identity (severity + message) with forced pagination. " +
        "Uses the attribution GeneratorDriver run result — distinct from project_diagnostics compile errors. " +
        "Filter by assemblyName + typeFullName from project_list_generators. Cursors bind to workspace epoch.")]
    public async Task<CallToolResult> ProjectListGeneratorDiagnostics(
        [Description("Roslyn projectId GUID string from workspace_list_projects.")]
        string projectId,
        [Description("Generator assembly name from project_list_generators.")]
        string assemblyName,
        [Description("Generator type full name from project_list_generators.")]
        string typeFullName,
        [Description("Page size (default 50, max 100).")]
        int? limit = null,
        [Description("Opaque nextCursor from a previous project_list_generator_diagnostics page.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("project_list_generator_diagnostics");

        if (!TryGetReadySession(out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _generators
            .ListGeneratorDiagnosticsAsync(
                session!,
                projectId,
                assemblyName,
                typeFullName,
                limit,
                cursor,
                cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return ErrorResult(ToPolicyError(error));
        }

        var page = success!.Page;
        return OkResult(new ProjectListGeneratorDiagnosticsResultDto
        {
            Generator = new GeneratorIdentityDto
            {
                AssemblyName = success.Identity.AssemblyName,
                TypeFullName = success.Identity.TypeFullName,
                Version = success.Identity.Version
            },
            Items = page.Items.Select(i => new GeneratorDiagnosticItemDto
            {
                Id = i.Id,
                Severity = i.Severity,
                Message = i.Message
            }).ToArray(),
            Truncated = page.Truncated,
            NextCursor = page.NextCursor,
            Message = page.Message,
            Epoch = session.Epoch
        });
    }


    [McpServerTool(Name = "project_list_dynamic_invocations"), Description(
        "List dynamic invocation / member / indexer sites in a C# or VB project, with static receiver and argument types when Roslyn knows them. " +
        "This is an IOperation call-site listing — not SymbolAttribution. Soft budget and epoch cursors apply.")]
    public async Task<CallToolResult> ProjectListDynamicInvocations(
        [Description("Roslyn projectId GUID string from workspace_list_projects.")]
        string projectId,
        [Description("Page size (default 50, max 100).")]
        int? limit = null,
        [Description("Opaque nextCursor from a previous project_list_dynamic_invocations page.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("project_list_dynamic_invocations");

        if (!TryGetReadySession(out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _dynamicInvocations
            .ListAsync(session!, projectId, limit, cursor, softBudget: null, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return ErrorResult(ToPolicyError(error));
        }

        return OkResult(new ProjectListDynamicInvocationsResultDto
        {
            Items = success!.Items.Select(i => new DynamicInvocationItemDto
            {
                Kind = i.Kind,
                FilePath = i.FilePath,
                Start = i.Start,
                Length = i.Length,
                ProjectId = i.ProjectId,
                ReceiverStaticType = i.ReceiverStaticType,
                ArgumentStaticTypes = i.ArgumentStaticTypes
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
