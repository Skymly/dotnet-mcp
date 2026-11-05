# Agent evaluation record

This file is the record `#528` asked for. It does **not** close `#528`, and it does not
reopen the 5.0.0 publish. `Skymly.DotNetMcp` 5.0.0 was tagged and pushed to NuGet on
2026-11-02 before this file existed. The per-category gate (tool-arm completion rate
at least the baseline, or a written reason and a return to the child issue, and no
publish) was not met.

Latency benches in [`oss-results.md`](oss-results.md) are from 2026-08-24, before the
5.0 tool surface. They are not this evaluation and they are not a 5.0 exit bench
(`#529` still asks for a refresh).

## What is not in this file

Rounds 1, 2, 3, and 5 were not readable from the checkout that wrote this record.
`C:\Temp\ab-eval`, `C:\Temp\ab-eval2`, `C:\Temp\ab-eval3`, and `C:\Temp\ab5` returned
access denied. No number from those rounds is repeated here. `#561`, `#562`, and
`#567` cite `ab-eval2` / `ab-eval3`; those measurements are not re-verified below.

`#528` also asked for an in-repo task bank (10–15 tasks, deterministic scorers,
MVVM/Avalonia, MAUI, a C#/F# mix, a plain C# library, n ≥ 3). That bank is not in
this repository. The readable rounds are n = 2, one subject (Observables), one model,
and two tasks (T3 callers, and T6 degraded diagnostics in round 4 only).

## Shared method (rounds 4 and 6)

- Model: `grok-4.7`, reasoning effort `xhigh`, identical task text, n = 2.
- Host: `codex exec` 0.164.0, isolated `CODEX_HOME`. The tool arm had only the
  dotnet-mcp stdio server. No extra tool instructions were appended to the prompt.
- Subject: an Observables copy with `bin/`, `obj/`, `.vs/`, `artifacts/`, and
  `TestResults/` excluded. `workspace_open` of `Observables.slnx` reached 293/293
  project units. No `.slnf` fallback.
- Control: the same model and host, no dotnet-mcp.
- A task with no final answer at 20 minutes counts as a failure.

Isolation was imperfect in both rounds: a directory deny blocked listing, not a read
of a known path, and the round 4 ACL could not be removed afterwards. The reports
still say the control transcripts did not contain the precheck JSON, the tool-arm
output, or the scoring notes. That is a method caveat, not a claimed leak.

## Round 4 — 2026-10-28 / 2026-10-29

Raw report: `C:\Temp\ab4-results\REPORT.md`.

Binary: `8eaf08c` (`ProductVersion` `5.0.0+8eaf08c4eef988a802b7dfd9486bb3d50c02cab1`).
This is before `#572`. Tasks: T3 callers (`#565`), T6 degraded diagnostics (`#566`).

| Task | Arm | Run | Answer | Seconds | Input tokens | MCP calls | Timeout |
|------|-----|-----|--------|--------:|-------------:|----------:|---------|
| T3 | control | r1 | correct | 98.5 | 272579 | 0 | no |
| T3 | control | r2 | correct | 72.8 | 183924 | 0 | no |
| T3 | tool | r1 | correct | 971.7 | 5693496 | 115 | no |
| T3 | tool | r2 | correct | 581.5 | 2011077 | 27 | no |
| T6 | control | r1 | no final answer | 1200.8 | — | 0 | yes |
| T6 | control | r2 | correct | 1141.3 | 3202563 | 0 | no |
| T6 | tool | r1 | correct | 200.0 | 731388 | 17 | no |
| T6 | tool | r2 | correct | 225.9 | 766298 | 17 | no |

T3 ground truth used by the report: `For<T>(CallInvoker)` callers in Grpc.Tests (4),
Grpc.Reactive.Tests (5), TrimTests (1), and four nuget-smoke programs. The nuget-smoke
programs are not in `Observables.slnx`. `For(Type, CallInvoker)` is only the generic
overload calling itself. All four T3 answers matched that set. No invented call sites.

T6 ground truth: `Observables.Events.Tests` listed 8 errors (1× CS0234, 7× CS1061)
while a declared generator dll was missing. An independent `dotnet build` of that
project was clean. Both tool runs marked the errors as workspace artifacts and did
not apply the stub they previewed. Control r2 reached the same "do not apply" outcome
by building. Control r1 produced no answer.

### Losses

- T3 cost. Both arms were correct. The tool arm took 582–972 s against 73–99 s, and
  2.0M–5.7M input tokens against 0.18M–0.27M. Correctness here is not a win on the
  `#528` time and token metrics.
- Coverage was unstable (`#565`, not yet `#572`). Unrelated projects collapsed
  ("and 260 other scoped projects with no usable reference") and no page with
  unsearched projects said `Caller page complete`. `Observables.Grpc.Reactive.Tests`
  was still missing from both `items` and the named list on the precheck (four TFMs)
  and on tool r2. Tool r1 sometimes put the five Reactive.Tests lines × three TFMs
  into `items` and sometimes did not. The final answers filled the gap with
  `symbol_resolve_at` and text search, not with the unsearched list.
- `#550`. Unscoped `symbol_resolve` of `HubAttribute`, `Observables.SignalR.HubAttribute`,
  and `GrpcService.For` returned `SoftBudgetExceeded`. The open was 293 units. The
  error text does not say 293. Non-blocking for these two tasks. `symbol_resolve_at`
  was the path the tool arm actually used to finish T3.

### What held

- T6 is the one category in this round where the tool arm beat the control on both
  completion and time: 2/2 correct in about 200–226 s, and it did not treat the false
  CS errors as source defects. `project_diagnostics`, `diagnostics_list_fixes`, and
  `diagnostics_preview_fix` all carried `degraded=true` and a reason naming
  `Observables.Events.R3.SourceGenerators.dll`.

## Round 6 — 2026-10-30

Raw report: `C:\Temp\ab6\results\REPORT.md`. Precheck: `C:\Temp\ab6\precheck\`.

Binary: `8f8492a` (merge of `#573` / `#572`). `ProductVersion`
`5.0.0+8f8492ac2b523bc586f079ed73fca0448a31fd89`. `serverInfo.version` was `5.0.0.0`
and did not include the SHA. Task: T3 only. Same prompt SHA-256 as round 4 T3
(`576ee13f7a5c934a88bb160713058af9e4242819304533489533d8d0babcd2c4`).

This binary is `#572`, not `#577`. Round 6 does not measure the compilation-snapshot
degraded check.

| | Control r1 | Control r2 | Tool r1 | Tool r2 |
|--|----------:|----------:|--------:|--------:|
| Answer | correct | correct | correct | correct |
| Seconds | 81.8 | 94.5 | 631.3 | 407.3 |
| Input tokens | 202118 | 285083 | 3403125 | 3481773 |
| MCP calls | 0 | 0 | 72 | 62 |
| Shell calls | 23 | 18 | 32 | 23 |

No invented callers. Both tool runs said the nuget-smoke programs are outside the
solution and still listed the right lines.

### Losses

- T3 cost again. 2/2 correct on both arms. The tool arm took 407–631 s against
  82–95 s, and about 3.4M input tokens against 0.20M–0.29M. Cached input was high
  (3.1M and 3.4M), so the multiplier is not all cold context, but the wall clock
  is still about 5–8×.
- Later pages were not stable. The controlled precheck (eight pages: four TFMs × two
  overloads, `entireSolution=true`) named `Observables.Grpc.Reactive.Tests` as
  searched-degraded: `was searched but its declared references never materialized
  (project.assets.json missing)`. Those pages did not put it in `items`. The first
  tool-arm page from `Observables.Grpc` matched that text. A later tool r2 page
  (`For<T>`, `entireSolution=true`, `limit=100`) had neither the Reactive.Tests rows
  nor the name — only "1 scoped project with no usable reference". A still later page
  in the same session put the 15 rows back into `items`. Disclosure is not the same
  shape on every query. `#577` is the follow-up for the live-file versus snapshot
  split; this round did not run that binary, and after `workspace_open` the copy
  still had no `project.assets.json` for Reactive.Tests, so this particular miss is
  not evidence for or against `#577`.
- `#550` was still `SoftBudgetExceeded` on the same three unscoped names in the
  precheck. The tool-arm event logs for this round did not hit that error again.

### What held

- On the controlled probe, `#572` named the project `#565` had been dropping.
  `Observables.Grpc.Tests` stayed in the declared-chain class, which matches the
  graph (it references `Observables.Grpc.R3`, not `Observables.Grpc`). Unrelated
  projects stayed collapsed. Those eight pages did not say `Caller page complete`.

## Cited, not re-measured

These are open product gaps. The A/B numbers behind them live in reports this file
could not read, so they are listed as issue claims, not as results.

- `#562` — rename writes compiler-visible references plus Exact XAML usages. It does
  not rewrite metadata-name strings, unbound short attribute names, docs, or
  PublicAPI files, and it does not list them. `unverifiedMentions` is not a field.
  The README and the initialize `instructions` now say so. The issue stays open.
- `#567` — apply does not return a narrow diagnostic delta, so an agent falls back
  to a full `project_diagnostics` and can time out on a large solution. Not
  re-measured here.
- `#561` — `Type.Method` resolve is inconsistent with other spellings. Not
  re-measured here.

## Gate

| `#528` / `#529` check | State on 2026-11-06 |
|-----------------------|---------------------|
| In-repo task bank, scorers, and run notes | missing |
| This file, including losses | this file; rounds 1–3 and 5 not included |
| Per category, tool-arm completion ≥ baseline, n ≥ 3, before publish | not met. Readable categories are T3 (tie on answers, tool arm loses on time and tokens) and T6 (tool arm ahead, one round, n = 2). 5.0.0 already shipped |
| Registry entry searchable | 5.0.0 publish returned 403. The packed name was `io.github.skymly/dotnet-mcp`; the granted namespace follows the owner login `Skymly`. Not re-published from this record |
| README `dnx` first, unpublished hedge removed | done in the 5.0.1 docs change; not a substitute for the registry entry |
| Bench refresh under the 20% p95 cap | not done. [`oss-results.md`](oss-results.md) is still 2026-08-24 |
