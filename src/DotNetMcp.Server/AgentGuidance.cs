using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotNetMcp.Server;

/// <summary>
/// Agent-facing guidance surface (#527): the initialize-result instructions plus the
/// two workflow prompts. Keeping the text here — beside the tool classes whose
/// contract it summarizes — makes drift visible in the same review instead of
/// scattering it across host wiring and docs.
/// </summary>
internal static class AgentGuidance
{
    /// <summary>
    /// Sent as <c>instructions</c> on the initialize result
    /// (<see cref="McpServerOptions.ServerInstructions"/>). Deliberately under ~1500
    /// characters: this lands in every client's system context, so it names the loop,
    /// the write rule, and the position model and stops.
    /// </summary>
    internal const string ServerInstructions =
        "dotnet-mcp: compiler-accurate .NET symbol tools (C# / VB / F#). For symbol work — navigation, " +
        "references, callers, implementations, attribution, rename — prefer these tools over text search: " +
        "grep cannot see overloads, partial classes, source-generated members, XAML bindings, or " +
        "cross-project references.\n" +
        "Loop: workspace_open (returns at once; loading runs MSBuild plus project analyzers — never open " +
        "untrusted trees) → poll workspace_status until phase = ready → symbol_resolve (name → " +
        "SymbolHandle) or symbol_resolve_at (path + line + column → SymbolHandle) → then symbol_summary, " +
        "symbol_goto_definition, symbol_source, symbol_attribution, symbol_find_references, " +
        "symbol_find_callers, symbol_find_implementations, symbol_type_hierarchy.\n" +
        "Writes are always preview → apply: symbol_preview_rename → symbol_apply_rename, " +
        "diagnostics_preview_fix → diagnostics_apply_fix, symbol_preview_refactoring → " +
        "symbol_apply_refactoring. There is no generic edit / write / shell. Review every preview slice — " +
        "including XAML slices and unverifiedXamlMentions on rename — before applying.\n" +
        "Positions are 1-based line / column in UTF-16 code units; endColumn is exclusive. A position " +
        "from one tool feeds the next verbatim. Paged results return nextCursor — continue from it " +
        "instead of restarting.";
}

/// <summary>
/// Workflow prompts (#527). Each renders a single user-role message that walks the
/// calling agent through the tool sequence, with the arguments interpolated so the
/// prompt is a runnable recipe rather than prose. Registered in
/// <see cref="ServerHost.AddDotNetMcp"/> like every other product primitive.
/// </summary>
[McpServerPromptType]
public static class AgentPrompts
{
    [McpServerPrompt(Name = "trace_symbol"), Description(
        "Trace a .NET symbol end-to-end: resolve by name or by source position, then summary, " +
        "attribution with generation triggers, and references including XAML usages.")]
    public static GetPromptResult TraceSymbol(
        [Description("Type or member name / FQN (e.g. SampleLib.Calculator). Provide either name, or path + line + column.")]
        string? name = null,
        [Description("Source file path under a trusted root. Combine with line + column instead of name.")]
        string? path = null,
        [Description("1-based line number. Combine with path + column instead of name.")]
        int? line = null,
        [Description("1-based column in UTF-16 code units. Combine with path + line instead of name.")]
        int? column = null)
    {
        var entry = BuildEntry(name, path, line, column);
        return UserMessage(
            "Trace a .NET symbol end-to-end with dotnet-mcp tools. They are compiler-accurate — " +
            "do not substitute text search for these steps.\n\n" +
            "Entry — resolve a verifiable SymbolHandle:\n" + entry + "\n\n" +
            "On the returned handle, in order:\n" +
            "1. symbol_summary — kind, containing symbol, projectId.\n" +
            "2. symbol_attribution — Handwritten vs SourceGenerator(Assembly::Type@Version), plus " +
            "triggers[] locating the handwritten code that caused a generated member (a partial " +
            "counterpart, a trigger attribute, or a XAML element). For a type check the members map too.\n" +
            "3. symbol_goto_definition — every declaration location, including source-generated trees.\n" +
            "4. symbol_find_references — every use site; Exact XAML usages (bindings, x:Class, element " +
            "types, x:Name, event handlers) follow source hits in the same paged stream with xamlUsage " +
            "set. Page with nextCursor instead of restarting.\n" +
            "5. As needed: symbol_find_callers (methods), symbol_find_implementations / " +
            "symbol_type_hierarchy (types), symbol_source for declaration text.\n\n" +
            "Report the handle, kind, origin + generator, trigger locations, and references grouped by file.");
    }

    [McpServerPrompt(Name = "safe_rename"), Description(
        "Rename a .NET symbol safely: resolve, preview the Workspace Edit including XAML slices and " +
        "unverifiedXamlMentions, apply, then re-check project_diagnostics.")]
    public static GetPromptResult SafeRename(
        [Description("SymbolHandle from symbol_resolve / symbol_resolve_at, or a type/member name to resolve first.")]
        string symbol,
        [Description("New identifier (unqualified).")]
        string newName) => UserMessage(
        $"""
        Rename "{symbol}" to "{newName}" through dotnet-mcp's preview → apply path — there is no direct write tool.

        1. Resolve a SymbolHandle if "{symbol}" is not already in language:projectId:signature#checksum form:
           symbol_resolve with the name, or symbol_resolve_at with path + 1-based line + 1-based column
           (UTF-16 code units). Renames cover handwritten C# / VB / F# symbols; SourceGenerator origin is
           refused except a generated x:Name field, which renames its XAML value plus handwritten references.
        2. Preview: call symbol_preview_rename with the handle and newName = "{newName}". It returns a
           previewId, per-document old/new slices, invalidatedHandles, and unverifiedXamlMentions.
        3. Review before applying — read every slice, including XAML (.axaml/.xaml) slices that keep
           bindings, x:Class, element/attribute names, x:Name values, and event handlers in sync.
           unverifiedXamlMentions are name-matched XAML usages the edit does NOT write; decide whether
           they need manual follow-up. Every handle in invalidatedHandles must be re-resolved after apply.
        4. Apply: symbol_apply_rename with the previewId while the workspace Epoch is unchanged and the
           preview unexpired. There is no apply path that skips preview.
        5. Re-check: project_diagnostics on the affected projects — expect no new errors — and
           symbol_find_references on the re-resolved handle when the rename touched public surface.
        """);

    private static string BuildEntry(string? name, string? path, int? line, int? column)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return $"- Call symbol_resolve with name = \"{name}\" (add projectId from " +
                "workspace_list_projects when several projects could match).";
        }

        if (!string.IsNullOrWhiteSpace(path) && line is not null && column is not null)
        {
            return $"- Call symbol_resolve_at with path = \"{path}\", line = {line.Value}, " +
                $"column = {column.Value}.";
        }

        return "- By name: symbol_resolve with the type/member name or FQN (add projectId when several " +
            "projects could match).\n" +
            "- By position: symbol_resolve_at with path + 1-based line + 1-based column in UTF-16 code " +
            "units — a position emitted by project_diagnostics, symbol_find_references, " +
            "symbol_find_callers, or symbol_goto_definition feeds it verbatim.";
    }

    private static GetPromptResult UserMessage(string text) => new()
    {
        Messages =
        [
            new PromptMessage
            {
                Role = Role.User,
                Content = new TextContentBlock { Text = text }
            }
        ]
    };
}
