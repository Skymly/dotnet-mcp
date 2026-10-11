# Changelog

All notable product changes are recorded here. Version numbers match `src/DotNetMcp.Server/DotNetMcp.Server.csproj`. A `vMAJOR.MINOR.PATCH` tag is not guaranteed for every published heading. Only bullets under `### Added`, `### Changed`, `### Fixed`, or `### Security` — or an unclassified bullet directly under `## Unreleased` — count as product entries for the version gate; documentation- and test-only entries go under `### Docs` or `### Tests`.

## Unreleased

### Changed

- symbol_preview_rename, diagnostics_preview_fix, and symbol_preview_refactoring report idempotentHint=false, since each call stores a new TTL previewId (`#444`)
- `message` on paged results is optional instead of required, matching the payloads that already omit it (`#443`)

### Fixed

- The CI version gate no longer treats docs/test-only Unreleased bullets as product entries; unclassified bullets still count (`#427`)
- Docs: ADR-0002 Amendment 5 names the current session interface without rewriting the accepted body (`#483`)
- Docs: README test command matches CI, states that transitive restore is not locked, and the Chinese section includes pack (`#481`)
- Tests: the packed README and the mcp-name comment match the server package identity (`#479`)
- Bench catalog matches recorded scenario ids, and open.ready is exempt from the 60s client-timeout gate (`#477`)
- Bench symbol guess no longer calls workspace_list_projects and discards the result (`#475`)
- Tests: tool annotations come from tools/list, and the CI version gate matches the C# package identity gate (`#473`)
- Tests: README tables, install paths, and the SDK feature band are parsed instead of matched as loose text (`#471`)
- Tests: success payloads deserialize by field, and a resolved XAML binding is absent from a complete diagnostics page (`#469`)
- Tests: the stdio smoke calls workspace_status and reads a WorkspaceNotReady error envelope (`#467`)
- Tests: single-TFM projects report TargetFramework, and temp-directory cleanup failures fail the test (`#465`)
- Tests: the in-process fixture requires an explicit trusted root and no longer treats the process working directory as one (`#463`)
- Tests: workspace status keeps loader warnings, reports load units and remaining time, and rejects an unsupported workspace extension (`#461`)
- Docs: --roots and DOTNET_MCP_TRUSTED_ROOTS form a union when both are set (`#459`)
- Tests: the production audit logger omits file contents, honors a disabled audit switch, and is what startup registers from DOTNET_MCP_AUDIT (`#457`)
- A rejected loaded project graph disposes the workspace instead of leaving it open (`#455`)
- Tests: path-policy guards cover the Unix root, Windows junctions, case differences, and the extended UNC prefix, and skip on the wrong OS instead of passing silently (`#453`)
- Graph-gate and apply-time path denials are written as path_policy_denied, with the tool name and the rejected path (`#451`)
- symbol_preview_rename on an F# handle reports an F# identifier error instead of telling the caller to pass a C# identifier (`#449`)
- Tests: a MAUI .xaml disk edit advances the workspace epoch, and xaml_diagnostics reads the new text (`#447`)
- XAML diagnostics classify language elements and x:Name by the XAML namespace, not the literal prefix x (`#445`)
- workspace_open keeps errorCode from the status payload instead of dropping it (`#441`)
- workspace and XAML path tools share one PathOutsideTrustedRoots message and suggested action (`#439`)
- Fix all says the document cap of 32 and the project application cap are fixed, instead of telling callers to raise a knob they cannot set (`#437`)
- diagnostics_list_fixes keeps same-title fixes from different providers when the equivalence key is empty, in an order that does not depend on assembly type enumeration (`#435`)
- diagnostics_preview_fix, symbol_preview_refactoring, and Fix all report a provider exception separately from a fix that produced no handwritten change (`#433`)
- Startup accepts `--roots=path` the same way as `--roots path`, rejects an empty roots value, and exits 1 with one JSON error line instead of a stack trace (`#431`)
- Dispose waits for an in-flight drift check or declared-path write before disposing the write lock, so shutdown does not surface ObjectDisposedException (`#429`)
- C# and VB rename rejects an illegal identifier with InvalidRenameName instead of previewing invalid source (`#292`)
- The changelog preface no longer claims every published heading has a matching vMAJOR.MINOR.PATCH tag (`#422`)
- README no longer calls the process working directory an implicit sandbox; it is not an implicit trusted root (`#420`)
- ADR-0004 status line lists Amendments 2 and 3, and an appended amendment names the current write surface (`#418`)
- Audit logs escape control characters in a caller-supplied path and truncate long paths (`#416`)
- The batch diagnostics budget is documented as the limit for project_diagnostics when projectId is omitted, not as Reserved (`#414`)
- The long-running details note no longer says product workspace_open returns jobId (`#412`)
- workspace_open and XAML path tools return an empty-path error for a blank path instead of PathOutsideTrustedRoots (`#410`)
- project_diagnostics batch mode reports a failed project as error and suggestedAction, not as a Severity=Error diagnostic (`#408`)
- diagnostics_preview_fix and symbol_preview_refactoring reject a fixIndex or refactoringIndex listed at an older epoch instead of selecting another action (`#404`)
- diagnostics_list_fixes says the list is built-in Features providers only, and the response sets IncludesProjectAnalyzers to false (`#401`)
- XAML diagnostic ranges cover the element or attribute name instead of a zero-width point (`#399`)
- Tests: MAUI diagnostics no longer treat ContentPage and Label as unknown elements, and the seam test checks that content (`#397`)
- xaml_diagnostics reports XAML0005 when x:DataType does not resolve, instead of dropping the binding check (`#395`)
- Binding type lookup no longer treats a constructor comment or assignment as DataContext (`#393`)
- xaml_diagnostics reports unknown properties on a resolvable unprefixed Window instead of skipping the root element (`#391`)
- xaml_diagnostics collects xmlns definitions once per call, inside the soft-budget clock, instead of once per binding (`#389`)
- XAML binding segments and x:Name resolution reuse the symbol already in hand instead of scanning the compilation again for each segment (`#387`)
- symbol_find_references and symbol_find_callers return CompilationUnavailable when a generated location cannot be reconciled, instead of labeling it Handwritten (`#385`)
- symbol_attribution on an F# handle returns GeneratorLanguageNotSupported instead of a synthetic Handwritten success (`#381`)
- F# snapshot capture prefers handwritten expanded documents over a raw Compile Include list (`#379`)
- A lost file watcher is restarted once, and workspace_status reports watcher as ok, lost, or off (`#377`)
- F# snapshot dictionaries are keyed by epoch, so one request cannot evict or overwrite another request's source text (`#374`)
- Epoch advances and the F# snapshot are published together, so a ready session cannot see a new epoch with the previous snapshot (`#372`)
- Watcher updates take the write lock so a stale drift repair cannot put old text back into the workspace (`#370`)
- FileSystemWorkspaceWatcher serializes start and stop so an overlapping stop cannot leave an active watcher (`#368`)
- Identical document text no longer counts as a workspace change or advances the epoch (`#366`)
- Tests: drop the unused coverlet.collector reference (`#364`)
- Bench scenario PeakWorkingSetMiB is the peak sampled during that scenario, not the process lifetime (`#362`)
- Bench help and docs no longer advertise `--allow-writes`; the harness does not measure apply (`#360`)
- Bench gates fail when no scenarios ran or a workspace open is missing or failed, instead of a vacuous 4/4 pass (`#358`)
- Tests: MSBuild SDK selection picks the newest install root that contains MSBuild.dll, and CI SDK versions are read from the dotnet-version list (`#356`)
- Tests: the version gate reads the on-disk CHANGELOG, not only csproj and server.json (`#354`)
- Tests: .fsi files follow Compile order, are captured by the directory fallback after .fs files, and advance epoch when deleted (`#352`)
- Tests: F# snapshot capture keeps an on-disk metadata reference path (`#350`)
- Tests: F# adapter source is scanned recursively for Roslyn solution access, including aliases, and a real fsproj open keeps the F# snapshot on the session epoch (`#348`)
- Tests: F# compile order is the fsproj Include order, not directory enumeration, and the reverse order fails FCS with FS0039 (`#346`)
- Tests: a mid-apply IO failure rolls the first file back byte-for-byte, including its BOM, and concurrent apply must succeed exactly once (`#344`)
- Tests: project Fix all budget reads DOTNET_MCP_BUDGET_FIXALL_PROJECT_MS and falls back when the value is invalid (`#342`)
- Tests: tampering a SymbolHandle field while keeping its checksum is rejected (`#340`)
- Tests: file-system watcher debounce above zero and Error-to-drift fallback fail if that wiring is removed (`#338`)
- Tests: apply's final path gate and the .slnf pre-open check each fail if that gate is removed (`#336`)
- Tests: MCP tool annotations now lock Destructive and Idempotent for every tool, not only ReadOnly and OpenWorld (`#334`)
- F# diagnostics name an unbuilt project-reference output instead of leaving only FS0039 (`#330`)
- F# conditional compilation uses MSBuild-evaluated DefineConstants, so SDK DEBUG is visible and unevaluated $(...) tokens are not passed through (`#328`)
- Dynamic invocation queries on an F# project return DynamicInvocationLanguageNotSupported instead of claiming the project is missing (`#326`)
- Generator queries on an F# project return GeneratorLanguageNotSupported instead of claiming the project is missing (`#324`)
- Dynamic invocation and XAML diagnostic pages report a soft-budget hit instead of a completed scan when the budget stops the scan early (`#321`)
- Apply refuses a no-BOM source file that is not valid UTF-8 instead of rewriting it with replacement characters (`#319`)
- `global.json` pins the .NET 10 feature band with `rollForward: latestFeature`; CI keeps floating 8/9 for fixtures and does not commit a restore lock (`#300`)
- Failed/idle query tools no longer tell the agent to wait until load completes (`#302`)
- Tests: graph-gate seam uses on-disk escaped documents; P0 exit-gate asserts payloads; XAML Zero budget is not labeled partial (`#301`)
- Handwritten document add/remove become Workspace Edit slices instead of `GeneratedDocument*Refused` (`#299`)
- `GeneratedSourcesPageCursor` binds the issuing tool so generated-source and generator-diagnostic pages cannot be chained (`#298`)
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

### Docs

- ADR-0001 Amendment 6 names the four product projects on disk; DotNetMcp.Workspace was never created and its duties live in DotNetMcp.Server (`#426`)

## 4.0.1 - 2026-09-12

Patch on the 4.0 line. `v4.0.0` was git-tagged only; this is the first intended NuGet publish of **`Skymly.DotNetMcp`**.

### Fixed

- Bench gates fail when no scenarios ran or a workspace open is missing or failed, instead of a vacuous 4/4 pass (`#358`)
- Tests: MSBuild SDK selection picks the newest install root that contains MSBuild.dll, and CI SDK versions are read from the dotnet-version list (`#356`)
- Tests: the version gate reads the on-disk CHANGELOG, not only csproj and server.json (`#354`)
- Tests: .fsi files follow Compile order, are captured by the directory fallback after .fs files, and advance epoch when deleted (`#352`)
- Tests: F# snapshot capture keeps an on-disk metadata reference path (`#350`)
- Tests: F# adapter source is scanned recursively for Roslyn solution access, including aliases, and a real fsproj open keeps the F# snapshot on the session epoch (`#348`)
- Tests: F# compile order is the fsproj Include order, not directory enumeration, and the reverse order fails FCS with FS0039 (`#346`)
- Tests: a mid-apply IO failure rolls the first file back byte-for-byte, including its BOM, and concurrent apply must succeed exactly once (`#344`)
- Tests: project Fix all budget reads DOTNET_MCP_BUDGET_FIXALL_PROJECT_MS and falls back when the value is invalid (`#342`)
- Tests: tampering a SymbolHandle field while keeping its checksum is rejected (`#340`)
- Tests: file-system watcher debounce above zero and Error-to-drift fallback fail if that wiring is removed (`#338`)
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
