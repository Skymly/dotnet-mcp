# Agent guidance snippet

Copy the fenced block below verbatim into the consuming repository's `AGENTS.md`,
`CLAUDE.md`, or equivalent agent-instructions file. It teaches an agent the
dotnet-mcp loop, the position model, and the write rule without it having to
discover them by trial and error. The same content rides the server's
initialize-result `instructions` and the `trace_symbol` / `safe_rename` prompts,
so an agent that reads server guidance needs nothing else.

```markdown
## dotnet-mcp

When the dotnet-mcp server is connected, prefer its compiler-accurate tools over
text search for anything about .NET symbols — grep cannot see overloads, partial
classes, source-generated members, XAML bindings, or cross-project references.

Standard loop:

1. `workspace_open` on a `.sln` / `.slnx` / `.slnf` / project file under a trusted
   root. It returns immediately — loading runs MSBuild and the project's
   analyzers/source generators, so only open trusted trees.
2. Poll `workspace_status` until `phase` = `ready`. Do not retry `workspace_open`
   while it is loading.
3. Resolve a `SymbolHandle`: `symbol_resolve` by name/FQN, or `symbol_resolve_at`
   by `path` + `line` + `column`.
4. Work from the handle: `symbol_summary`, `symbol_goto_definition`,
   `symbol_source`, `symbol_attribution` (handwritten vs generated, with
   `triggers[]` back to the code that caused a generated member),
   `symbol_find_references` (includes exact XAML usages), `symbol_find_callers`,
   `symbol_find_implementations`, `symbol_type_hierarchy`.

Positions: `line`/`column` are 1-based, columns count UTF-16 code units (a tab is
one column), `endColumn` is exclusive. A position emitted by one tool feeds the
next tool verbatim. Paged results return `nextCursor` — continue from it instead
of restarting.

Writes are always preview → apply: `symbol_preview_rename` → `symbol_apply_rename`,
`diagnostics_preview_fix` → `diagnostics_apply_fix`,
`symbol_preview_refactoring` → `symbol_apply_refactoring`. There is no generic
edit/write/shell tool. Review every preview slice before applying — including
XAML slices and `unverifiedXamlMentions` on rename — then re-check
`project_diagnostics` after an apply that touched compiled code.
```
