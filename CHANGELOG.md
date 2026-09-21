# Changelog

All notable product changes are recorded here. Version numbers match `src/DotNetMcp.Server/DotNetMcp.Server.csproj` and git tags `vMAJOR.MINOR.PATCH`.

## Unreleased

### Fixed

- `workspace_check_drift` / watch-lost take the apply write mutex so an in-flight apply cannot be rolled back to OldText (`#297`)
- Ready F# snapshot stays populated across epoch bumps; deleting `.fs` / `.fsi` advances Epoch before recapture (`#296`)
- `server.json` omits unpublished NuGet/dnx install packages; version gate rejects Unreleased product entries that still use the previous release (`#295`)
- Version gate reads the csproj `<Version>` and requires `.mcp/server.json` plus the latest CHANGELOG heading to match; CI checks the same three places (`#245`)
- README Quick Start leads with `dotnet run` / local pack; `dnx Skymly.DotNetMcp` is documented as available after NuGet publish (`#246`)
- F# `symbol_find_callers` fills `CallerHandle` with the enclosing member, not the callee (`#247`)
- F# snapshot commits with ready/epoch; sessions no longer capture disk with `trustedRoots: null` (`#249`)
- F# rename apply reads snapshot text from the F# workspace snapshot so real `.fsproj` loads can write `.fs` files (`#252`)
- F# `_snapshotTexts` drops paths that are not in the current snapshot so deleted files cannot be queried after reload (`#250`)
- F# member handles include a parameter signature so overloads no longer collapse `symbol_attribution` (`#251`)
- F# rename `newName` is validated with FCS `PrettyNaming.IsIdentifierName` (`#253`)
- F# checker is notified of file changes only when snapshot text actually changes (`#244`)
- Graph gate failures surface `LoadedGraphOutsideTrustedRoots` on status and query tools (`#255`)
- Query tools suggest `workspace_open` when idle and do not tell the agent to poll forever when failed (`#264`)
- `symbol_resolve` without `projectId` no longer early-exits on a warm unique hit, and a soft-budget miss is `SoftBudgetExceeded` not `SymbolNotFound` (`#259`)
- Page cursors bind tool + query identity; exhausted pages do not emit `nextCursor` even when the soft budget hit (`#260`)
- Malformed XAML is `XamlParseError`; sibling `x:DataType` no longer leaks; `.axaml`/`.xaml` disk edits advance epoch (`#262`)
- Partial types attribute every declaring tree; handwritten origin survives generator-driver failures; find-refs scans source-generated documents (`#256`, `#257`, `#258`)
- Apply serializes `WriteDeclaredPaths` so a failing rollback cannot clobber a winning write (`#261`)
- F# compiler args include `MetadataReferences` and project-reference output paths so real `.fsproj` graphs are not FS0039 (`#248`)
- All 31 tools declare MCP annotations (`readOnly` / `destructive` / `idempotent` / `openWorld=false`) (`#263`)
- Project-scope Fix all reports leftover same-Id diagnostics instead of a successful partial preview (`#283`)

### Security

- Graph gate checks AnalyzerReferences against trusted roots plus dotnet / NuGet toolchain roots; MetadataReferences stay unchecked (read-only metadata) (`#254`)

## 4.0.1 - 2026-09-12

Patch on the 4.0 line. `v4.0.0` was git-tagged only; this is the first intended NuGet publish of **`Skymly.DotNetMcp`**.

### Fixed

- Find-refs / callers soft-budget cancel no longer treats the document table as exhausted; `ms<=0` budgets fall back to the ADR default instead of emitting a stuck cursor (`#242`)
- Scoped find-refs and `symbol_find_callers` walk dependents (plus the defining project); callers takes `entireSolution` like find-refs (`#242`)
- Workspace Edit apply maps I/O failures to `*ApplyFailed`, restores the previewId, rolls back the in-progress file, and preserves encoding/BOM (`#242`)
- `CompilationLru` in-flight compiles are no longer bound to the first caller's cancellation token (`#242`)
- `xaml_diagnostics` no longer flags property elements / attached properties as unknown (`#242`)
- `symbol_resolve` without `projectId` matches test-like project names by segment, not substring (`#242`)
- Batch `project_diagnostics` pages past 100 and surfaces per-project compile failures instead of looking clean (`#242`)
- Document Fix all reports leftover after the 32-application cap and skips EquivalenceKey mismatches instead of stopping the document (`#242`)
- Empty / illegal paths in `TrustedRoots.Contains` return a structured policy error instead of throwing out of `workspace_open` (`#242`)
- F# snapshots freeze `<Compile>` order, defines, and `.fs` / `.fsi` disk changes (Epoch advances even when Roslyn has no F# documents) (`#242`)
- `project_list_generator_diagnostics` reports generator exceptions as `MCPGEN0001` Error rows; a driver-level failure maps to `CompilationUnavailable` instead of a clean empty page
- `PathPolicy` attribute-read failures fail closed (`PathPolicyException`), except missing path components which still append lexically
- Ambiguous generator attribution on identical content is refused instead of binding the wrong generator (`#224`)
- XAML symbol resolve is scoped to the document's project (`#225`)
- Preview store sweeps expired entries; Apply no longer holds the store lock across disk I/O (`#227`)
- Production FileSystemWatcher subscribes to `Error` and falls back to `CheckDrift` on overflow (`#227`)

### Changed

- Docs: real `.fsproj` `symbol_resolve` is a required fixtures gate (`docs/perf/optimization.md`, `docs/perf/benchmark.md`)
- ADR-0001 Amendment 3 no longer says F# still reads from `WorkspaceSession.Solution` (Amendment 5 already moved the snapshot)
- New policy error codes: `XamlDocumentAmbiguous`, `RenameApplyFailed`, `WorkspaceEditApplyFailed`

### Security

- Graph gate also checks `AdditionalDocuments` (`#226`)
- F# snapshot skips reparse points and checks trusted roots before reading compile items (`#226`)
- `WorkspaceHost` Dispose joins the in-flight load; FSW Stop clears the callback (`#226`)
- Drift / FSW / watch roots only read paths under trusted roots; `\\?\` prefixes are stripped before the prefix check (`#227`)
- ADR-0004 Amendment 5: `.sln` / `.slnx` / single-project graph gate remains post-load (out-of-root `ProjectReference` is evaluated, then rejected). `.slnf` stays pre-open (`#241`)

## 4.0.0 - 2026-09-02

First tagged 4.0 line. Shipping identity is **`Skymly.DotNetMcp`** (`dotnet-mcp` remains the tool command). The previous NuGet id `dotnet-mcp` is occupied by an unrelated unlisted package.

### Added

- Code Refactoring tools: `symbol_list_refactorings` / `symbol_preview_refactoring` / `symbol_apply_refactoring`
- `diagnostics_preview_fix(scope=project)` (project Fix all)
- `McpServer` package type and embedded `.mcp/server.json`
- Windows CI job; pack step verifies package id, `McpServer` type, and `server.json`
- MSBuild fixture sampling for VB resolve, F# attribution, Avalonia `xaml_resolve_class`, and source-generator attribution
- `CHANGELOG.md` and an English Quick Start

### Changed

- Trusted roots are mandatory (`--roots` or `DOTNET_MCP_TRUSTED_ROOTS`). Process CWD is no longer an implicit root
- Path canonicalization is fail-closed on unresolvable reparse points; loaded graphs and apply paths are re-checked
- `ILanguageAdapter` now owns `GetAttributionAsync`; MCP tools no longer take `RoslynLanguageAdapter` directly
- `RoslynLanguageAdapter` split into query / finders / attribution / diagnostics / rename partials

### Security

- See ADR-0004 Amendment 4

## 3.0.0

Diagnostic fix tools (`diagnostics_list_fixes` / `diagnostics_preview_fix` / `diagnostics_apply_fix`) and F# rename.

## 2.0.0

Restricted Workspace Edit (C# / VB rename preview-apply), VB.NET read-side, MAUI XAML, F# / COM / `dynamic` read-side.

## 1.0.0

P0 C# read-side: stdio host, trusted roots, MSBuild workspace load, symbol navigation, project diagnostics, source-generator list/source/diagnostics/attribution. GitHub release tag `v1.0.0`.
