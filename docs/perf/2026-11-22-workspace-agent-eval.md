# Workspace tool profiles in four agent tasks

The candidate reduced first-list and preview response sizes. Median input tokens also fell in all four natural tasks. Correctness was 11/12 in both arms, and median wall time rose in every task. The candidate improved A3 from 2/3 to 3/3 and fell from 3/3 to 2/3 on D1. All six candidate edit runs wrote through the host. The directed clients completed both semantic apply workflows.

## Scope and method

This comparison uses the published [task bank](task-bank/README.md), original English prompts (including the workspace line), pinned Git exports and unchanged operator-side grader from `700efb973e27132cae4ea91377611b9921bb8487`. A1 traces a CommunityToolkit command to its generator. A3 renames a handwritten property and two typed Avalonia bindings. D1 repairs a seeded compiler error. C1 is a literal lookup in Serilog. For A3 and D1 the grader checks all source bytes and builds a fresh golden twin. Files in `bin/` and `obj/` are excluded. C1 uses the Serilog library project with the whole pinned checkout available.

Both arms run Codex CLI 0.166.0 against OpenCode Go `deepseek-v4.1-flash` on WSL Ubuntu 24.04 with .NET SDK 10.0.303. A cell gets a fresh Codex home, source copy, server process, and 1,200 seconds. All four tasks run three times per arm. The baseline uses the default full profile. The candidate sets `DOTNET_MCP_TOOL_PROFILE=lite` on the server alone. Other Codex settings, tools enabled in the host, restored inputs and prompt are held fixed. The provider selects the model behind its hosted alias. No provider weight digest is available.

At the baseline, all three named edit families already shared the preview and apply lifecycle, with in-memory rollback on write exceptions. Typed Avalonia bindings already joined semantic rename. The server advertised its full tool set and could identify metadata declarations. The F# reference scan covered the defining project and its F# dependents and reported C#/VB coverage gaps.

The worker has its own PID, network and mount namespaces. Source is read-only for A1 and C1, with writable build scratch. A3 and D1 can edit their disposable subjects. The worker sees the SDK, Codex binary, read-only restored NuGet cache and the extracted MCP tool. A mounted Unix socket accepts inference requests only for the chosen model. The controller inserts the provider credential outside the worker. Raw Codex events, answers, errors and source copies stay in private per-cell directories. The public ledger contains metrics, source and prompt fingerprints, MCP tool paths and edit evidence.

The C1 pilot passed the original grader with a required baseline MCP connection in 10.310 seconds, 33,761 input tokens, 317 output tokens and zero MCP calls. The preflight rebuilt the clean CommandSample. D1 produced CS0117 before repair and compiled after restoring the correct member. The pilot and directed probes are excluded from task scores.

Collection started November 22 and resumed November 24. The first attempt at candidate A1 repetition 3 was interrupted before the controller received a final Codex event or answer. Its raw transcript was retained in the private captures. The matrix includes the replacement attempt for that slot, with the same package, prompt, source and time budget. The two completed candidate cells from November 22 were kept.

## Natural task results

The [cell ledger](task-bank/2026-11-22-workspace-profiles.jsonl) records each task, grade, wall time, input and output tokens, MCP call count, ordered tool selections, successful writes, host patch paths and source changes. The [probe ledger](task-bank/2026-11-22-workspace-probes.jsonl) records the separate pilot and directed discovery run.

The time, token and MCP call columns show medians across all three repetitions, including failed grades. The zero-MCP and edit-path columns count runs.

| Task | Profile | Correct | Seconds | Input tokens | Output tokens | MCP calls | Zero MCP | Edit path | Infra | Timeout |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: |
| A1 | full | 3/3 | 54.6 | 213,719 | 2,337 | 8 | 0 | 3 none | 0 | 0 |
| A1 | lite | 3/3 | 58.4 | 145,051 | 1,593 | 7 | 1 | 3 none | 0 | 0 |
| A3 | full | 2/3 | 83.7 | 220,455 | 4,306 | 0 | 3 | 3 host | 0 | 0 |
| A3 | lite | 3/3 | 129.8 | 84,737 | 2,634 | 1 | 1 | 3 host | 0 | 0 |
| D1 | full | 3/3 | 107.4 | 328,194 | 5,512 | 0 | 3 | 3 host | 0 | 0 |
| D1 | lite | 2/3 | 241.3 | 196,095 | 3,332 | 4 | 1 | 3 host | 0 | 0 |
| C1 | full | 3/3 | 13.6 | 33,894 | 399 | 0 | 2 | 3 none | 0 | 0 |
| C1 | lite | 3/3 | 22.1 | 24,518 | 359 | 0 | 3 | 3 none | 0 | 0 |

On A1, two candidate agents opened the workspace, resolved the generated command and called `symbol_attribution`. The third answered from source with zero MCP calls. On D1, two candidate agents used workspace navigation and `project_diagnostics` before writing through the host. The third used host tools alone. Five out of six edit runs passed in each arm. The six host edits per arm are counted in the table independently of their grade.

Candidate A3 repetition 3 requested a compact rename preview and passed its ID to `symbol_apply_rename`. The Codex host refused the call because it required approval under the fixed `approval_policy = "never"`. The agent then finished through host edits and passed the grader. The ledger records this as a host approval rejection on the MCP call and a successful host edit for the cell. The independent A3 stdio probe sent the apply to the server and passed the same grader.

Baseline A3 repetition 3 changed indentation on two XAML lines and failed the exact source comparison. Candidate D1 repetition 3 produced the expected source bytes through a host edit and reported `CS1061` in its answer. The unchanged grader required `CS0117` and recorded an answer mismatch. That candidate used zero MCP calls.

In the directed discovery prompt, Codex asked `tool_help` for the generators, XAML, diagnostics and refactoring groups and then for the corresponding named tools. The subsequent calls returned the generator attribution, one XAML binding segment and three refactorings. The call to `diagnostics_list_fixes` reached the server and returned `DiagnosticNotFound` for the clean project. The discovery probe passed with 19 MCP calls in 183.067 seconds. The separate directed D1 probe used the seeded compiler diagnostic and completed that fix workflow.

## Build identity

| Component | Identity |
| --- | --- |
| Baseline Git | `700efb973e27132cae4ea91377611b9921bb8487` |
| Baseline package SHA-256 | `98e6fea99c5e718db00c831123e3a8180431da90899d8b610c59c981bcbda4a7` |
| Baseline server DLL SHA-256 | `ebc826ea8a42357cfc80cf20531e1993789340211de564b042e7348cd8d1d100` |
| Candidate Git | `55e3e1f49e985aa5d4f375acbfab737339b1098d` |
| Candidate package SHA-256 | `2bf96526b9570102cb8f557471401cb5c773c32a3194b227c2c792f7014d0c0a` |
| Candidate server DLL SHA-256 | `00e11497d82e1d8a8ceccd819810bc35a44807eab5d082bad79d472b588ae100` |
| Codex CLI SHA-256 | `b8a11fedf24b2dfb59a70c9e52b030f9a508dcd49cd2dd0d41956f4c785156b8` |
| Task bank Git blob | `f726693dfeb87bc88e73e2939a0ea6bbd639064d` |
| Grader Git blob | `a6d499f8029d9c4b66d69e578c0469caea842bab` |

## Directed product acceptance

The stdio probes used the extracted candidate DLL in the table above. A3 and D1 used the task-bank grader. The figures below count incoming UTF-8 JSON-RPC frames including the newline. A3 used one preview ID per format on the same source and epoch. Use a fixed working path when reproducing exact counts because responses include source paths. [Directed probe measurements](task-bank/2026-11-22-workspace-directed.json)

| Response | Full | Lite or compact | Reduction |
| --- | ---: | ---: | ---: |
| Candidate first `tools/list` | 39,362 B, 36 tools | 13,877 B, 21 tools | 64.7% |
| Candidate A3 rename preview | 19,663 B | 2,827 B | 85.6% |

The baseline full catalog had 33 tools and measured 35,846 B. The candidate full catalog retains all 33 names and adds three tools. A3's compact response covered five C# changes and two typed XAML changes on its first page. The probe applied its ID, checked the entire source tree byte for byte, and built the grader's golden tree. This includes the separate ReactiveUI property and unchanged formatting. D1 found Roslyn's spelling correction for `CS0117`, reviewed the default lite preview (879 B), applied it, and verified the diagnostic disappeared before the same byte-and-build grader passed.

| Change | Candidate commit | Acceptance |
| --- | --- | --- |
| Paged Workspace Edit previews | `47f974fb1f88cbaa16a1b3afc23ccf1fe29e5092` | A3 over stdio, all three preview/apply families, empty changes, large files with split UTF-16 diff fragments, multiple file pages, stale epochs and expiry. See [PreviewReviewTests](../../tests/DotNetMcp.Tests/PreviewReviewTests.cs) and [WorkspaceEditTests](../../tests/DotNetMcp.Tests/WorkspaceEditTests.cs). |
| Lite profile | `237ef1b4b37d5ce9b33801f3c8fb824d3bd62e16` | Stdio catalog comparison, grouped diagnostic help through D1, named tool invocation, and separate Codex discovery probe. See [StdioHostSmokeTests](../../tests/DotNetMcp.Tests/StdioHostSmokeTests.cs). |
| Recovery after process termination | `2c83115030eab12d4fa0d31dbafcc1ca350bd53f` | Kill after the first or last write and restart twice. Verified exact original bytes for rollback, exact intended bytes for completed edits, and preserved manual changes with a named conflict. Recovery rechecks roots and redirects. See [WorkspaceEditRecoveryTests](../../tests/DotNetMcp.Tests/WorkspaceEditRecoveryTests.cs). |
| Bound metadata | `3bbdbbab5dba6bcbc7b8b00e3277ffb304cb403c` | Local NuGet feed with two versions across three project and TFM instances, overloads, XML documentation availability, and separate identities through two aliases for the same named API. See [BoundMetadataTests](../../tests/DotNetMcp.Tests/BoundMetadataTests.cs). |
| F# signatures and references | `55e3e1f49e985aa5d4f375acbfab737339b1098d` | Checked .fs/.fsi pairs report located hidden declarations and FCS errors. The C#/VB fixture finds exact consumer sites, distinguishes unused and unavailable coverage, and checks a stale assembly that still exports a now-hidden name. See [FSharpSignatureStatusTests](../../tests/DotNetMcp.Tests/FSharpSignatureStatusTests.cs) and [the mixed-solution regression](../../tests/DotNetMcp.Tests/MsBuildWorkspaceIntegrationTests.cs). |

Windows stdio returned `confirmed` for `scale` in both consumers at `CsConsumer/Use.cs:5` and `VbConsumer/Use.vb:3`. Both reported `noHits` for `unused` and `unavailable` for the implementation-only `factor`. WSL reproduced both locations. On that host the design-time VB compilation reported `BC30002` for missing VB `My` support types, so the VB project marked coverage `unavailable` and still returned the confirmed site. The directed WSL probe asserts the specific diagnostic and the confirmed location.

Coverage is limited to compiler-bound named references in handwritten C#/VB documents. A missing or incompatible consumer compilation lowers coverage. The cross-language rename guard still applies. Metadata inspection reads local compiler-bound PE references and their available XML docs. Recovery covers process termination on a local filesystem with atomic replacement. Termination during staging can leave a temporary file.

The final Windows Release suite passed 1,012 tests with three platform-specific skips and zero failures. The packed smoke test was among the passing tests and launched the package through `dotnet tool exec`. The two updated F# regression tests also passed on WSL in a separate checkout. The final verification changes after the candidate SHA are limited to tests and evaluation evidence.

## Reproduction

Use a Linux x64 Ubuntu 24.04 host or WSL distro with Python 3.12+, Git, bubblewrap, curl and npm. The host must allow unprivileged user namespaces. Put the runtime and output on a Linux filesystem that supports Unix sockets. The worker needs only the directories created below. Use the checked-in grader, runner and relay from this report's checkout.

```bash
runtime="$HOME/dnmcp-eval-runtime"
private="$HOME/dnmcp-eval-private"
packages="$private/packages"
base_server="$private/baseline-server"
candidate_server="$private/candidate-server"
base_sha=700efb973e27132cae4ea91377611b9921bb8487
candidate_sha=55e3e1f49e985aa5d4f375acbfab737339b1098d
mkdir -p "$runtime"/{dotnet,codex,eval-nuget,subjects} "$private" "$packages"
chmod 700 "$private"

curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$runtime/dotnet-install.sh"
bash "$runtime/dotnet-install.sh" --version 10.0.303 --architecture x64 --install-dir "$runtime/dotnet"
export PATH="$runtime/dotnet:$PATH"
(cd "$runtime/codex" && npm pack --silent @openai/codex@0.166.0-linux-x64 && tar xf ./*.tgz)
git clone https://github.com/AvaloniaUI/Avalonia.Samples "$runtime/subjects/avalonia"
git clone https://github.com/serilog/serilog "$runtime/subjects/serilog"
avalonia_clone="$runtime/subjects/avalonia"
serilog_clone="$runtime/subjects/serilog"
dotnet_mcp_clone=/path/to/dotnet-mcp
```

When the key is already in the Windows process environment, start the Linux controller from the same PowerShell process. Pass only the variable name to WSL. Bubblewrap clears the worker environment, and the credential is consumed by the host-side proxy in memory.

```powershell
$env:WSLENV = (@($env:WSLENV, 'OpenCodeGo') -ne '' -join ':')
wsl -d Ubuntu-24.04
```

Both server packages use .NET SDK 10.0.303 in Release mode. The same extracted NuGet-tool layout is mounted at `/server` in both arms. To hydrate a new trusted cache use the preparation command below with `--hydrate`. Subsequent preflights can use the package cache offline. Raw captures and setup-only network access stay on the controller. Each cell gets a private network namespace with only its inference relay.

```bash
# Run once per SHA in a clean worktree and extract each resulting nupkg.
# Baseline worktree
dotnet pack src/DotNetMcp.Server -c Release -o "$packages/base"
python3 -m zipfile -e "$packages/base/Skymly.DotNetMcp.5.1.0.nupkg" "$base_server"
# Candidate worktree
dotnet pack src/DotNetMcp.Server -c Release -o "$packages/lite" -p:PackageVersion=5.1.0-review.g55e3e1f
python3 -m zipfile -e "$packages/lite/Skymly.DotNetMcp.5.1.0-review.g55e3e1f.nupkg" "$candidate_server"

bank=/path/to/report-checkout/docs/perf/task-bank
common=(--runtime "$runtime" --avalonia "$avalonia_clone" --serilog "$serilog_clone" --repo "$dotnet_mcp_clone")
python3 "$bank/profile_prepare.py" "${common[@]}" --hydrate

# The calling host supplies OpenCodeGo in its environment.
# On Windows, add only the variable name to WSLENV before invoking WSL.
python3 "$bank/profile_trial.py" "${common[@]}" --profile full --sha "$base_sha" \
  --server "$base_server/tools/net10.0/any" --out "$private/pilot" --pilot
python3 "$bank/profile_trial.py" "${common[@]}" --profile full --sha "$base_sha" \
  --server "$base_server/tools/net10.0/any" --out "$private/matrix"
python3 "$bank/profile_trial.py" "${common[@]}" --profile lite --sha "$candidate_sha" \
  --server "$candidate_server/tools/net10.0/any" --out "$private/matrix"
python3 "$bank/profile_trial.py" "${common[@]}" --profile lite --sha "$candidate_sha" \
  --server "$candidate_server/tools/net10.0/any" --out "$private/discovery" --discovery
python3 "$bank/profile_results.py" "$private/matrix/cells.jsonl"
```

Run the directed acceptance on the extracted candidate. With an offline worker cache already hydrated, pass its path through `NUGET_PACKAGES` and `RestoreSources` to the build and restore commands used by the probes.

```bash
server="$candidate_server/tools/net10.0/any/DotNetMcp.Server.dll"
python3 "$bank/preview_probe.py" --repo "$avalonia_clone" --compare-profiles dotnet "$server"
python3 "$bank/diagnostic_probe.py" --repo "$dotnet_mcp_clone" dotnet "$server"
python3 "$bank/fsharp_references_probe.py" dotnet "$server"
```

Use `--allow-degraded-vb` for the documented WSL design-time error. To run the packaged tool smoke test along with the suite, set `DOTNET_MCP_PACKED_TOOL_SOURCE` to the candidate package directory and run `dotnet test DotNetMcp.slnx -c Release`.

After an interrupted controller run, keep the existing ledger. Once the worker has stopped, move the unfinished cell's unrecorded directory to a separate private path and add `--resume` to the same command. The runner verifies each recorded cell against the requested build, model, host, input and prompt before skipping it.

Wall time runs from worker launch through the final Codex event. Input tokens include provider-cached input. Tool counts use distinct started Codex tool item IDs. MCP counts exclude protocol negotiation and `tools/list`. An edit counts as MCP apply when the tool returns a preview ID and non-empty written paths. A subsequent host patch can make the path mixed. A changed source tree without a successful MCP apply counts as a host edit. A run with zero MCP calls can pass through host tools. Infrastructure failure and timeout are recorded independently of the grader.

The per-cell run order is A1, A3, D1, C1 in each arm. Cells ran serially. Baseline collection started while the features were in development, so all baseline cells preceded candidate cells. Timing can reflect load from other development work on the same machine and provider-side changes. Three trials per task support a descriptive comparison. The natural comparison changes the binary and profile together. The same-binary wire probes isolate the tool-list and preview-format effects.
