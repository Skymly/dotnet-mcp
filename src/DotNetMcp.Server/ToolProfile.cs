using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotNetMcp.Server;

/// <summary>A fixed shortlist for clients that cache tools/list for the entire conversation.</summary>
internal static class ToolProfile
{
    public const string Variable = "DOTNET_MCP_TOOL_PROFILE";

    public static bool IsLite(string? value) =>
        string.Equals(value, "lite", StringComparison.OrdinalIgnoreCase);

    private sealed record Entry(string Group, string Summary);

    private static readonly Dictionary<string, Entry> Lite = new(StringComparer.Ordinal)
    {
        ["workspace_open"] = new("navigation", "Open a solution or project under trusted roots. MSBuild, analyzers and generators execute. Never open untrusted code. Poll workspace_status."),
        ["workspace_status"] = new("navigation", "Poll until ready. Inspect load failures and warnings."),
        ["workspace_read_preview"] = new("navigation", "Continue a compact preview by file index or diff offset. Review remaining pages before apply."),
        ["workspace_list_projects"] = new("navigation", "List project IDs, languages and target-framework instances."),
        ["symbol_resolve"] = new("navigation", "Find C#, VB or F# SymbolHandles by qualified or simple name."),
        ["symbol_resolve_at"] = new("navigation", "Resolve a code or typed XAML reference by 1-based line and UTF-16 column."),
        ["project_inspect_metadata"] = new("navigation", "Follow a metadata result into the selected project's bound assembly, overloads and XML docs."),
        ["symbol_find_references"] = new("navigation", "Find compiler references and exact typed XAML usages. Continue with nextCursor."),
        ["fsharp_signature_status"] = new("navigation", "Check an F# .fs against its .fsi and locate hidden declarations or compiler errors."),
        ["symbol_attribution"] = new("generators", "Identify the source generator, generated member and handwritten trigger for a symbol."),
        ["xaml_resolve_binding"] = new("xaml", "Resolve a binding path in its Avalonia or MAUI XAML context."),
        ["project_diagnostics"] = new("diagnostics", "Find compiler diagnostics and locations. Use diagnostics_list_fixes for a repair."),
        ["diagnostics_list_fixes"] = new("diagnostics", "List built-in fixes for one diagnostic and location. Preview the index before the epoch changes."),
        ["diagnostics_preview_fix"] = new("diagnostics", "Preview a listed fix. Review changes and use diagnostics_apply_fix to commit."),
        ["diagnostics_apply_fix"] = new("diagnostics", "Write the reviewed fix by previewId."),
        ["symbol_preview_rename"] = new("rename", "Preview a semantic rename including exact XAML bindings. Review all changes, then call symbol_apply_rename."),
        ["symbol_apply_rename"] = new("rename", "Write the reviewed rename by previewId."),
        ["symbol_list_refactorings"] = new("refactoring", "List built-in refactorings at a handwritten symbol."),
        ["symbol_preview_refactoring"] = new("refactoring", "Preview a listed refactoring and review its changes."),
        ["symbol_apply_refactoring"] = new("refactoring", "Write the reviewed refactoring by previewId."),
    };

    public static void RegisterLite(IMcpServerBuilder builder)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in typeof(ServerHost).Assembly.GetTypes()
                     .Where(t => t.IsDefined(typeof(McpServerToolTypeAttribute))))
        {
            foreach (var method in type.GetMethods())
            {
                var name = method.GetCustomAttribute<McpServerToolAttribute>()?.Name;
                if (name is null || !Lite.TryGetValue(name, out var entry))
                    continue;

                details.Add(name, method.GetCustomAttribute<DescriptionAttribute>()!.Description);
                builder.Services.AddSingleton(sp => McpServerTool.Create(method,
                    request => ActivatorUtilities.GetServiceOrCreateInstance(request.Services ?? sp, type),
                    new() { Services = sp, Description = entry.Summary }));
            }
        }

        builder.WithTools(new LiteHelp(details));
        builder.Services.Configure<McpServerOptions>(o => o.ServerInstructions =
            "Open trusted code with workspace_open and poll workspace_status. Resolve by name or position. " +
            "tool_help gives workflows for generators, xaml, diagnostics, rename, refactoring and navigation. " +
            "Review the whole preview, then use its matching apply tool with previewId to write the edit.");
    }

    // Explicit registration keeps this helper out of the default full profile.
    private sealed class LiteHelp(Dictionary<string, string> details)
    {
        [McpServerTool(Name = "tool_help", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
        [Description("Find named tools by group (navigation, generators, xaml, diagnostics, rename, refactoring), or get detailed help for one tool.")]
        public CallToolResult Help([Description("Group or exact tool name. Omit to list groups.")] string? topic = null)
        {
            if (topic is null)
                return McpToolEnvelope.OkResult(Lite.Values.Select(e => e.Group).Distinct().ToArray());
            if (details.TryGetValue(topic, out var description))
                return McpToolEnvelope.OkResult(new { tool = topic, description });

            var matches = Lite.Where(p => p.Value.Group == topic)
                .Select(p => new { name = p.Key, description = p.Value.Summary }).ToArray();
            return matches.Length > 0 ? McpToolEnvelope.OkResult(matches)
                : McpToolEnvelope.ErrorResult(new PolicyErrorDto
                {
                    Error = "UnknownHelpTopic",
                    Message = "No matching lite tool or group.",
                    SuggestedAction = "Call tool_help without a topic for the group names."
                });
        }
    }
}
