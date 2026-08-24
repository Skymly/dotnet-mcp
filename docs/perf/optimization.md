# Performance optimization plan

Phase 0–3 implemented in product code (finder batching, callers closure, LRU warm, F# disk snapshot, `.slnf` guidance). OSS retest 2026-08-24: Serilog callers p95 2.2 ms (was 1718), Spectre scoped refs p95 2.5 ms (was 1290). Phase 4 is still future work.

Grounded in `docs/perf/oss-results.md`, Spike S2, ADR-0002 / ADR-0003.
Does **not** reopen ADR-0002 scheme D (persistent index as source of truth).
Does **not** make `workspace_open` a blocking `tools/call`.

## What the numbers actually say

| Layer | Observation | Optimize? |
|-------|-------------|-----------|
| `workspace_open` return | ~40 ms on 6–190 projects | No. Already non-blocking. |
| Ready (MSBuild graph) | 2.4 s → 19 s with project count | Only product-level (open `.slnf` / fewer TFMs). Do not compile 190 projects at load. |
| Warm summary / goto-def / members | 1–4 ms at every scale | No. |
| First `symbol_resolve` | 0.3–0.9 s when the Compilation LRU is cold | Yes, cheap: warm the opened closure after `ready`. |
| Find References / callers | 0.2–1.7 s, symbol-dependent | **Yes. This is the product query budget.** |
| Batch `project_diagnostics` | 50–614 ms | Later. Soft budget 15 s still has margin. |
| F# `symbol_resolve` on real `.fsproj` | `SymbolNotFound` + ~1.6 s | Correctness first: `FSharpWorkspaceSnapshot` is empty when Roslyn has no F# documents. |

The hot implementation shape (not solution size) is the main leak:

```text
for each Document in scope:
    SymbolFinder.FindReferencesAsync(symbol, solution, {that one document})
    // same pattern for FindCallersAsync — and callers walk the *entire* Solution
```

Soft budget / Epoch cursor need a document index. They do **not** need one Roslyn finder call per document.

## Constraints (do not violate)

- One request = one `IWorkspaceSession` / Epoch (ADR-0002).
- Compilation LRU stays host-owned, default 50. Do not expose a memory budget to Core.
- List/scan tools return partial results + `nextCursor`, never a 60 s hang (ADR-0003).
- Attribution may run generators a second time; cache stays `(projectId, Epoch)`.
- Persistent index is not the answer for v0. If revisited later it can only be advisory, never the read-side source of truth.

## Phase 0 — query scanner (highest leverage)

Owner: `RoslynLanguageAdapter` + `FindRefsScopes`.
Acceptance: re-run the four OSS subjects; Serilog `Log` callers p95 **< 300 ms** (was 1718); Spectre `AnsiConsole` scoped refs p95 **< 500 ms** (was 1290); no change to page DTO / cursor Epoch rules.

1. **One finder call per page, not per document.**
   Build the remaining document set from `docIndex`, call `FindReferencesAsync` / `FindCallersAsync` **once** with that set, flatten, then slice by `locOffset` + `limit`. Keep the same cursor encoding so existing clients do not change.
2. **Callers default to the defining project's dependency closure.**
   Today callers ignore `FindRefsScopes`, walk every `Solution` document, and use the 20 s entire-solution Soft budget. Align with Find References: closure + 5 s; entire-solution remains opt-in if we ever add a flag (do not add a new MCP tool).
3. **Optional same-Epoch hit cache** keyed by `(Epoch, handle, scope)`.
   First page pays the finder; `nextCursor` reuses the flattened hit list until Epoch advances. Invalidate with the compilation LRU on Epoch bump. This is an implementation cache, not a workspace index.

Do not parallelize one-doc finder calls. That multiplies Compilation pressure and fights the LRU.

## Phase 1 — first-query latency (Compilation LRU)

Owner: `WorkspaceHost` + `CompilationLru`.
Acceptance: FluentValidation first `symbol_resolve` p95 **< 80 ms** after `ready` + 500 ms (was 256 ms); Observables first resolve **with `projectId`** stays < 50 ms.

1. After phase becomes `ready`, background-compile the **opened project's dependency closure** (cap at LRU capacity, default 50). Never delay `workspace_status`.
2. `symbol_resolve` without `projectId` must not compile the whole Workspace. Prefer: exact FQN via `GetTypeByMetadataName` on already-warm compilations, then only cold-compile projects whose name/TFM is a plausible host. Ambiguity behavior stays.
3. Bench / audit: record `compilationsStarted`, `lruHits`, `lruEvictions` per tool call (no source text). The OSS Observables spike to ~650 MiB was the bench probing every project, not the Agent-with-`projectId` path.

## Phase 2 — F# snapshot (correctness = perf)

Owner: `WorkspaceSession.CaptureFSharp` / `MsBuildSolutionLoader`.
Acceptance: `MixedWithFs` + a real `.fsproj` `symbol_resolve FsLib.Widget` succeeds; fixtures suite can mark that row `required`.

MSBuildWorkspace often does not put `.fs` files on `Project.Documents` (`LanguageNames.FSharp` never appears). The FCS adapter then searches an empty `FSharpWorkspaceSnapshot`. Capture `.fs` paths from the `.fsproj` / project assets (or directory enumeration under the project folder) when freezing the snapshot at Epoch, still **beside** `Solution`, not through `session.Solution`.

## Phase 3 — load wall-clock (product, not a new cache)

Owner: docs + `workspace_open` guidance. Ready ~19 s on 190 ProjectIds is MSBuild evaluation. Compiling during load would make it worse.

1. Prefer `.slnf` or a single `.csproj` when the Agent is working in one area. Document this next to the tool description.
2. Do **not** add “compile all projects on open”.
3. Revisit ADR-0002 scheme D only if a measured Agent first-turn (open → status → resolve → summary) exceeds ~25 s **after** Phases 0–1 **and** `.slnf` is unacceptable. Any index must be a hint; answers still come from the current Epoch snapshot.

## Phase 4 — diagnostics / attribution (only if Phase 0–1 land)

- Batch `project_diagnostics`: bounded parallel `GetCompilationAsync` (DOP 2–4), still one Soft budget, still one Epoch.
- Attribution: keep the two-pass model; first-hit cost is already cached per Epoch. Do not switch to FilePath heuristics (ADR-0001).

## What not to do

- BenchmarkDotNet on `SymbolHandle` parse / path policy.
- Raising Compilation LRU to “unlimited” on 190-project solutions.
- Making `workspace_open` wait for ready because 19 s is sometimes under 60 s (no margin).
- Per-document parallel `SymbolFinder` as a “speedup”.
- Treating Spike S2 `S2.Bench` as the product harness (use `benches/DotNetMcp.Bench`).

## Verification

Same four OSS workspaces, same symbols, ` --suite scale --iterations 5 --warmup 1`.
Compare `docs/perf/oss-results.md`. Phase 0 is done only when the two bold targets above hold and `fixtures` / `smoke` gates still pass.

