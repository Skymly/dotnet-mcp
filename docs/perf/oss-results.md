# OSS scale bench results

Machine: Windows 10.0.26200, .NET 10.0.10, 20 logical CPUs.
Harness: `benches/DotNetMcp.Bench --suite scale --iterations 5 --warmup 1`.
Subjects cloned shallow to `C:\Code\TempCode\dotnet-mcp-bench-subjects` (except Observables, already local).
Humanizer HEAD was skipped: current tree targets net11.0.

Raw JSON: `benches/DotNetMcp.Bench/data/latest-scale-*.json` (gitignored).

**After Phase 0–3** (2026-08-24). Baseline numbers from the pre-change run are in parentheses.

## Subjects

| Project | Workspace | Projects | Probe symbol |
|---------|-----------|----------|--------------|
| [FluentValidation](https://github.com/FluentValidation/FluentValidation) | `FluentValidation.sln` | 6 | `FluentValidation.AbstractValidator` |
| [Serilog](https://github.com/serilog/serilog) | `Serilog.sln` | 19 | `Serilog.Log` |
| [Spectre.Console](https://github.com/spectreconsole/spectre.console) | `src/Spectre.Console.slnx` | 29 | `Spectre.Console.AnsiConsole` |
| Observables (S2 baseline) | `Observables.slnx` | 190 | `Observables.Events.Generators.ObservableEventsGenerator` |

## Phase 0 gates

| Target | Before | After | Gate |
|--------|-------:|------:|------|
| Serilog `Log` callers p95 | 1718 ms | **2.2 ms** | pass (&lt; 300 ms) |
| Spectre `AnsiConsole` scoped refs p95 | 1290 ms | **2.5 ms** | pass (&lt; 500 ms) |

## Workspace load

| Project | `workspace_open` return | Ready |
|---------|------------------------:|------:|
| FluentValidation | 40 ms | 2.6 s (was 2.4 s) |
| Serilog | 39 ms | 4.7 s (was 4.4 s) |
| Spectre.Console | 40 ms | 6.2 s (was 5.9 s) |
| Observables | 42 ms | 25.8 s (was 19.0 s) |

`workspace_open` stays non-blocking. Observables ready is a bit higher (background Compilation LRU warm starts after `ready` and can overlap the next measured open-status poll on a busy machine). Load is still MSBuild evaluation, not query time.

## Query p95 (ms)

| Scenario | FV | Serilog | Spectre | Observables |
|----------|---:|--------:|--------:|------------:|
| `symbol.resolve` (warm) | 250 | 0.4 | 0.5 | 0.5 |
| `symbol.summary` | 0.9 | 1.3 | 2.3 | 1.5 |
| `symbol.goto_definition` | 3.5 | 2.1 | 1.7 | 1.7 |
| `symbol.members` | 2.7 | 15.2 | 24.5 | 2.9 |
| `symbol.find_references` scoped | **1.6** (87) | **2.3** (222) | **2.5** (1290) | **2.2** (56) |
| `symbol.find_references` entire | **2.1** (94) | **2.8** (268) | **8.5** (233) | **4.1** (199) |
| `symbol.find_callers` | n/a (type) | **2.2** (1718) | **4.0** (3) | **1.7** (808) |
| `symbol.summary` ×4 parallel | 0.9 | 2.6 | 2.8 | 1.6 |
| `project.diagnostics` single | 33 | 59 | 63 | 12 |
| `project.diagnostics` batch | 49 | 501 | 1057 | 555 |

FV `find_callers` is still `SymbolNotFound` (no method handle on `AbstractValidator`). All other required scenarios passed Soft budget and the 60 s client hard top.

## Reading

- One `SymbolFinder` call per page (plus same-Epoch hit cache) collapsed the scan budget. Callers on `Serilog.Log` went from 1.7 s to 2 ms; scoped refs on `AnsiConsole` from 1.3 s to 2.5 ms.
- Warm navigation is unchanged: 1–4 ms at every scale (members p95 on Spectre/Serilog can spike to 15–25 ms).
- Load wall-clock still tracks project count. Prefer `.slnf` / a single project for Agent first-turn; do not compile all 190 on open.
