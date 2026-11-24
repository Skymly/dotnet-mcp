# Task-level evaluation (#528)

The [workspace tool profiles report](../2026-11-22-workspace-agent-eval.md) reuses these prompts and this grader to compare the baseline full configuration with the candidate lite configuration. It includes the isolated controller commands, the cell ledger and the directed stdio probes.

This is a blinded, paired **agent** experiment, not the latency benchmark. The
bank has ten tasks. The operator owns the bank, exported inputs and grader;
give the agent **only** its exported subject and the output of `prompt`.

| Subject (immutable commit and subtree in `bank.json`) | Tasks |
|---|---|
| Avalonia.Samples / CommandSample, CommunityToolkit.Mvvm | A1 generated command; A2 typed binding; A3 rename including XAML, not the identically named ReactiveUI property |
| MVVM-Samples / MAUI + its core view models | M1 activity indicator; M2 result label and handwritten implementation |
| Mixed F#/C# solution | F1 direct call locations; F2 cross-language consumer |
| Plain SDK solution with a seeded typo | D1 repair a diagnostic without changing library APIs |
| Serilog | C1 literal lookup; C2 enum order (deliberate text controls) |

## Operator setup

Requires Python 3.12+, Git, the pinned subject clones, and a Release build of
`DotNetMcp.Server`. Before timing, verify the required SDKs/packages/workloads
on the worker (including MAUI). Build or restore known-good inputs in a
disposable copy. D1 **before** the edit must fail specifically with CS0117;
the unseeded project and A3 baseline must build. If setup fails, record an
infrastructure block, not a wrong agent answer. Both arms get identical
prepared sources and restore state. No `--cold` on anyone's working tree.

```sh
python evaluate.py prepare D1 --repo /clones/dotnet-mcp --work /worker/new-subject
python evaluate.py prompt D1
```

`prepare` uses `git archive` of the pinned tree, not uncommitted working files.
It refuses to reuse an existing directory. Prepare each repetition and arm
separately. Do not export the whole dotnet-mcp repo to the worker.

For M1/M2, keep the MAUI repository root so the app's relative resource paths
resolve. Git archives omit the documentation submodule. Populate
`msdocs-communitytoolkit/` from [MicrosoftDocs/CommunityToolkit](https://github.com/MicrosoftDocs/CommunityToolkit)
at `74b3cf6f01ff357bc8fc39437d888cf31e07b347` in both arms before restoring.
Check that the worker can load the .NET 9 MAUI target and the shared core
project. A loaded app without restored references does not pass preflight.

## Run

1. Pin the host, model/version, effort, server commit/binary and time limit
   **before** the first run. Default plan: 20 minutes per cell, three
   repetitions per task per arm (60 cells). Start fresh sessions. Alternate
   which arm runs first by task and repetition. No parallel timing on one host.
2. Use the same host built-ins and identical `prompt` output for both arms.
   Control: no MCP servers. Tool: add **only** `dnmcp`, stdio command `dotnet`,
   arguments `["/absolute/DotNetMcp.Server.dll","--roots","/worker/new-subject"]`.
   The binary and configured root must be inside the isolated worker. Verify
   the tool connects there; its own initialize instructions are part of the
   treatment. Do not require agents to use it or give one arm hints.
3. Use a separate worker/VM or equivalent read isolation. The bank, oracle,
   other arm's transcript, host credentials and grader **must not be readable**
   from it. A different working directory or a write-only restriction is not
   evidence of read isolation. Keep networking off except the chosen inference
   endpoint. For A3/D1 allow writes to the disposable subject; other tasks are
   read-only. Do not relax the sandbox to obtain a score.
4. Example host invocation inside that worker (Codex CLI; explicitly configured
   local model shown). Use separate clean `CODEX_HOME`s with the arm-specific MCP
   configuration above. The controller supplies the prompt on stdin, enforces
   the timeout, and keeps stdout/stderr and the last message outside the subject.

   ```sh
   codex exec --oss --local-provider ollama -m "$MODEL" --ephemeral \
     --skip-git-repo-check --sandbox read-only -C "$SUBJECT" --json \
     --output-last-message "$LAST_MESSAGE" - < "$PROMPT" > "$EVENTS"
   ```

5. Count each started cell, including timeouts; no retrying a bad result to
   replace it. Save wall seconds, input/output tokens from host usage (state
   whether cached input is included), and host tool calls including shell and
   MCP. An infrastructure error is not a completion and prevents gate closure.
   A missing final answer or mismatched JSON fails the task.

`local_trial.py` is a single-cell controller for the loopback Ollama case. It
does not import the bank, pull models, use hosted credentials or start a matrix.
It records JSONL and wall time, counts distinct host tool-call item IDs, and
uses Codex's reported input tokens (including cached input) and output tokens.
Give it the same executable, model digest and deadline in both arms. Pass
`--server /absolute/DotNetMcp.Server.dll` only for the tool arm, and `--edit`
in **both** arms for A3/D1. An `infra` receipt means inspect stderr and stop;
do not mark an unreadable/missing worker result correct. This wrapper is not
a substitute for isolating the worker from the oracle.

```sh
python local_trial.py --codex /path/to/codex --model "$MODEL" \
  --subject /worker/new-subject --out /controller/new-cell < "$PROMPT"
```

## Score outside the worker

```sh
python evaluate.py grade F1 --answer /results/last-message.json
python evaluate.py grade D1 --answer /results/last-message.json \
  --repo /clones/dotnet-mcp --work /results/exported-trial-tree
python evaluate.py summary /results/cells.jsonl
```

Read tasks use exact JSON equality; F1 requests sorted sites and C2 requests
declaration order. Edit tasks additionally require the expected complete source
tree, with only the specified token substitutions. The grader builds a fresh
golden twin **after** proving source equality; it never executes an
agent-created target or trusts `bin/obj`. This deliberately rejects unrelated
reformatting and changes to homonyms. For A3 the reference edit names the five
C# declaration/use sites and two binding sites in the pinned revision by
one-based line. It leaves the helper comment unchanged; changing that comment,
the independent ReactiveUI property, or the project file fails the byte check.
Keep historical receipts with their original bank and grader hashes rather
than pooling or rescoring runs across rule versions. The boolean grade comes from this
controller, not from an agent's own assessment.

One JSON line per cell: `task`, `arm` (`control`/`tool`), `rep` (1..3),
`model`, `host`, `candidate` (same candidate identifier in both arms),
`budgetSeconds`, `status` (`finished`/`timeout`/`infra`), `passed`,
`seconds`, `inputTokens`, `outputTokens`, `toolCalls`.
`null` metrics are allowed only for unfinished attempts; the aggregate does
not invent missing costs. Keep sanitized per-cell results and report all
categories, including losses. Missing/duplicate cells, mixed identities,
infrastructure failures or a category with fewer tool-arm completions
cannot yield a passing gate. A category with zero correct answers in both
arms is inconclusive, not evidence of parity. Pilot and self-test data do not belong in the
acceptance ledger.

## Directed preview acceptance

Run `python preview_probe.py --repo /path/to/Avalonia.Samples --compare-profiles dotnet /absolute/path/to/DotNetMcp.Server.dll` from this directory. The probe exports A3 at the bank's pinned revision, restores the project, and starts fresh stdio sessions to measure the first tools/list response for each profile. It measures complete JSON-RPC response frames including the newline. In the full profile it requests both preview formats on the same symbol and Epoch, reads all compact pages, applies that preview ID, and runs the existing byte-for-byte grader and golden build. `--parent` retains the subject and allows a fixed path for repeat measurements.

## Mixed-language reference acceptance

`python fsharp_references_probe.py dotnet /absolute/path/to/DotNetMcp.Server.dll` builds the six-project `FsharpCrossProject` fixture in a disposable directory and checks every MCP page. The three queries cover a public F# function used by C# and VB, an unused public function, and an implementation value omitted by the .fsi. The `fsharp_references_confirm_consumer_bindings_and_reject_a_stale_export` test also builds a prior public API and checks the consumer bound to that obsolete export.
