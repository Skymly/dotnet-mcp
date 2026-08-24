# Product benchmark suite

Measures **agent-facing** MCP tool latency, payload size, truncation, and process memory for `dotnet-mcp`.
This is the product harness. Spike S2 (`spikes/s2-scale`) remains historical evidence for ADR-0002/0003
(`.slnx` load, Compilation LRU, Find References scope). Do not extend S2.

Vocabulary follows root `CONTEXT.md`: Workspace, WorkspaceSession, Epoch, Soft budget, SymbolHandle,
Workspace Edit, Attribution, Drift, ILanguageAdapter.

## Why not BenchmarkDotNet

Workspace load is 15–20 s on a ~190-project solution. Soft budget, `workspace_open` non-blocking,
pagination cursors, and MCP JSON envelopes are the product numbers. A custom harness records those
directly. Isolated microbenchmarks (handle parse, path policy) may be added later; they are not this suite.

## Layers

| Layer | Surface | When |
|-------|---------|------|
| L1 Core | `SymbolQueryService` / `IWorkspaceSession` | Future isolation of envelope overhead |
| **L2 Host + MCP (this harness)** | In-process MCP client → product tools | Default. Same envelope the agent sees |
| L3 stdio process | Real `dotnet-mcp` child process | Optional later; not required to ship the suite |
| L4 Scale | External solution via `--solution` | Manual; never CI |

## Suites

| Suite | Workspace | CI |
|-------|-----------|----|
| `smoke` | 2-project synthetic | Optional local; not in GitHub Actions |
| `fixtures` | `tests/fixtures` SampleFilter + MixedWithFs + AvaloniaApp | Local default |
| `synthetic` | Generated N×M C# graph | Local scaling curve |
| `scale` | `DOTNET_MCP_BENCH_SOLUTION` or `--solution` (S2 used Observables.slnx) | Manual |

`all` = fixtures + synthetic (not scale).

## Metrics (every scenario)

- Wall clock: min / mean / p50 / p95 / max (warmup discarded)
- Peak WorkingSet MiB and allocated bytes for the iteration
- MCP payload bytes
- Result cardinality + `truncated` / `nextCursor` (Soft budget)
- Error code if the tool returned a policy error
- Assigned Soft budget vs 60 s client hard top (ADR-0003)

Cold vs warm: first post-load call is `*.cold`; repeats are `*.warm`.
`workspace_open` is always measured as **return latency** (must stay non-blocking) and **ready latency**
(status poll to `ready`).

## Scenario catalog

### Workspace

- `workspace.open.return` — tools/call returns while load still runs
- `workspace.open.ready` — poll `workspace_status` to `ready`
- `workspace.list_projects`
- `workspace.check_drift`

### Symbol (C# / VB / F#)

- `symbol.resolve.{cold,warm}`
- `symbol.summary` · `symbol.goto_definition` · `symbol.members` · `symbol.attribution`
- `symbol.find_references.scoped` · `symbol.find_references.entire`
- `symbol.find_callers` · `symbol.find_implementations` · `symbol.type_hierarchy`

### Project

- `project.diagnostics.single` · `project.diagnostics.batch`
- `project.list_generators` · `project.list_generated_sources` · `project.list_generator_diagnostics`
- `project.list_dynamic_invocations`

### XAML

- `xaml.resolve_class` · `xaml.list_xmlns` · `xaml.resolve_name` · `xaml.resolve_binding` · `xaml.diagnostics`

### Workspace Edit (preview only)

Apply is off unless `--allow-writes`. Default measures `symbol.preview_rename`,
`symbol.list_refactorings`, `diagnostics.list_fixes` when a diagnostic exists.

### Concurrency

- `symbol.summary.parallel4` — four overlapping summaries on one Epoch snapshot (ADR-0003 §5)

## Budgets and gates

Defaults match `SoftBudgetOptions` / ADR-0003:

| Class | Budget |
|-------|--------|
| `workspace_open` return | < 500 ms (non-blocking) |
| Single-project compile / goto-def / most symbol tools | 5 s |
| Scoped Find References | 5 s |
| Entire-solution Find References | 20 s |
| Batch diagnostics | 15 s |
| Any tools/call | < 60 s client hard top |

Gate outcomes: `pass` / `warn` (p95 > 50% of budget) / `fail` (p95 > budget or > 60 s, or open blocked).
`--no-gates` records gates but always exits 0 after a completed run.

## How to run

```powershell
dotnet run --project benches/DotNetMcp.Bench -c Release -- --suite fixtures
dotnet run --project benches/DotNetMcp.Bench -c Release -- --suite synthetic --projects 20 --files 8
dotnet run --project benches/DotNetMcp.Bench -c Release -- --suite scale --solution $env:DOTNET_MCP_BENCH_SOLUTION --symbol Some.Type
dotnet run --project benches/DotNetMcp.Bench -c Release -- --suite smoke
```

Useful flags: `--iterations 5` `--warmup 1` `--filter substring` `--cold` `--out benches/DotNetMcp.Bench/data` `--allow-writes` `--no-gates`.

JSON lands in `--out` as `{stamp}-{suite}.json` and `latest-{suite}.json`. `--cold` deletes `bin`/`obj` under the opened workspace root.

## Adding a scenario

1. Open the matching suite method in `benches/DotNetMcp.Bench/Suites.cs`.
2. Call `ctx.MeasureToolAsync(...)` with a stable `id`, tool name, args, and `BudgetClass`.
3. Mark `required: false` if the fixture may legitimately return a policy error (e.g. empty Binding path).
4. Re-run `fixtures` and confirm the new row appears in the JSON.

## Out of scope

- Extending `spikes/s2-scale` (frozen evidence)
- Applying Workspace Edit in the default suite
- GitHub Actions scale jobs
- BenchmarkDotNet micro-suite
- Persistent index / disk cache experiments (rejected in ADR-0002 scheme D for v0)

## Fixture notes (from the first harness run)

- `SampleFilter` and `MixedWithFs` load via MSBuild and cover C# / VB / project / Workspace Edit preview.
- F# `symbol_resolve` on a real `.fsproj` may return `SymbolNotFound`: MSBuildWorkspace does not populate F# documents into the Roslyn Solution, so `FSharpWorkspaceSnapshot` can be empty. The scenario is recorded but not a required gate. `project_diagnostics` on the F# project still runs.
- XAML tools need the `.axaml` in the workspace snapshot (`AdditionalFiles`). The fixtures suite generates `XamlApp` rather than using `AvaloniaApp.csproj` (that project does not include the axaml as a document). `xaml_resolve_name` needs a name generator; `xaml_resolve_binding` needs `x:DataType` — both are optional.
- `symbol_find_callers` is only issued against a method/function/property handle (types return `SymbolNotFound`).
- Workspace Edit **apply** is out of the default suite (preview only).

Related: query/load optimization plan in [optimization.md](optimization.md).

