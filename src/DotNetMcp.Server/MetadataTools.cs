using System.ComponentModel;
using DotNetMcp.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotNetMcp.Server;

[McpServerToolType]
public sealed class MetadataTools(WorkspaceHost host, IAuditLogger audit)
{
    [McpServerTool(Name = "project_inspect_metadata", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Inspect a type in the local PE references actually bound by the selected C#/VB project and target framework. Returns assembly identity, declared public/protected signatures, overloads and available XML documentation. Use the metadata fields from symbol_resolve_at or symbol_goto_definition.")]
    public async Task<CallToolResult> Inspect(
        [Description("Project instance GUID from workspace_list_projects.")] string projectId,
        [Description("Exact CLR metadata name, including namespace, generic arity and + for nested types.")] string typeName,
        [Description("Exact assembly identity to disambiguate a type.")] string? assemblyIdentity = null,
        [Description("Optional simple member name. Returns its overloads.")] string? memberName = null,
        int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
    {
        audit.ToolInvoked("project_inspect_metadata");
        if (!McpToolEnvelope.TryGetReadySession(host, out var session, out var unavailable))
            return unavailable!;
        var project = LanguageAdapters.FindProjectById(session.Solution, projectId);
        if (project is null || !RoslynLanguageAdapter.IsSupportedRoslynLanguage(project.Language) || string.IsNullOrWhiteSpace(typeName))
            return McpToolEnvelope.ErrorResult(new PolicyErrorDto
            {
                Error = "InvalidMetadataQuery",
                Message = "Inspection requires a C#/VB project instance and CLR type name.",
                SuggestedAction = "Select projectId from workspace_list_projects and copy typeName from the symbol's metadata navigation."
            });
        var (result, error) = await RoslynLanguageAdapter.InspectMetadataAsync(session, project, typeName,
            assemblyIdentity, memberName, limit, cursor, cancellationToken).ConfigureAwait(false);
        return error is not null ? McpToolEnvelope.ErrorResult(McpToolEnvelope.ToPolicyError(error))
            : McpToolEnvelope.OkResult(new
            {
                projectId,
                projectName = project.Name,
                targetFramework = ProjectSummary.ExtractTfm(project),
                scope = "Bound PE reference, declared public/protected members. XML is present when supplied by the reference.",
                type = result
            });
    }
}
