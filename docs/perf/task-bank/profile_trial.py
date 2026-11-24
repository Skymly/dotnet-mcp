"""Run the four-task MCP profile comparison in a Linux bubblewrap worker.

Operator-side only. See docs/perf/2026-11-22-workspace-agent-eval.md for inputs.
"""

import argparse
import contextlib
import hashlib
import http.server
import json
import os
from pathlib import Path
import shutil
import signal
import socketserver
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
import uuid

import evaluate


MODEL = "deepseek-v4.1-flash"
TASKS = ("A1", "A3", "D1", "C1")
TIMEOUT = 1200
HERE = Path(__file__).resolve().parent
DISCOVERY_PROMPT = """Workspace: CommandSample.csproj
Use the connected MCP server to demonstrate its read-only tool discovery. Ask its on-demand help
about generator attribution, XAML binding, diagnostic fixes and refactorings. Call each of the
corresponding tools on this sample. For attribution, trace the generated OpenThePodBayDoorsCommand
on CommunityToolkitCommandsViewModel. For XAML, inspect its CommunityToolkit view's RobotName
binding. Locate a real diagnostic for the fix listing if available; if the project is clean, call
the fix listing at a handwritten declaration and report its actual response. List refactorings on
a handwritten property. Do not change files. Return JSON listing the tools you invoked and whether
each produced a result, an empty set or an error.
"""
URL = "https://opencode.ai/inference/go/openai/v1/responses"


@contextlib.contextmanager
def model_socket(key, socket_path):
    """Worker may stream responses for this model only. No auth material crosses the socket."""
    session_id = str(uuid.uuid4())

    class Proxy(http.server.BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_POST(self):
            if self.path != "/v1/responses":
                return self.send_error(404)
            body = self.rfile.read(int(self.headers["Content-Length"]))
            if json.loads(body).get("model") != MODEL:
                return self.send_error(400)
            req = urllib.request.Request(URL, body, {"Authorization": "Bearer " + key,
                "Content-Type": "application/json", "Accept": "text/event-stream",
                "User-Agent": "codex/0.166.0 dotnet-mcp-eval", "x-opencode-session": session_id})
            try:
                with urllib.request.urlopen(req, timeout=TIMEOUT) as response:
                    self.send_response(response.status)
                    self.send_header("Content-Type", response.headers.get_content_type())
                    self.end_headers()
                    while data := response.read1(65536):
                        self.wfile.write(data)
                        self.wfile.flush()
            except urllib.error.HTTPError as exc:
                self.send_error(exc.code)
            except urllib.error.URLError:
                self.send_error(502)
            except (BrokenPipeError, ConnectionError, TimeoutError):
                return

        def address_string(self):
            return "local"

    class ModelServer(socketserver.ThreadingUnixStreamServer):
        daemon_threads = True

    with ModelServer(str(socket_path), Proxy) as server:
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            yield
        finally:
            server.shutdown()


def worker(args, subject, home, socket_path, edit):
    sdk = args.runtime / "dotnet"
    codex = next((args.runtime / "codex").rglob("bin/codex"))
    mounts = []
    if not edit:
        projects = {p.parent for suffix in ("csproj", "fsproj", "vbproj")
                    for p in subject.rglob("*." + suffix)}
        for project in sorted(projects):
            for name in ("obj", "bin"):
                path = project / name
                path.mkdir(exist_ok=True)
                mounts += ["--bind", str(path), str(Path("/subject") / path.relative_to(subject))]
    return ["bwrap", "--die-with-parent", "--unshare-all", "--clearenv",
            "--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp",
            "--ro-bind", "/usr", "/usr", "--symlink", "usr/bin", "/bin",
            "--symlink", "usr/lib", "/lib", "--symlink", "usr/lib64", "/lib64",
            "--ro-bind", "/etc/passwd", "/etc/passwd", "--ro-bind", "/etc/group", "/etc/group",
            "--ro-bind", str(codex), "/opt/codex", "--ro-bind", str(sdk), "/opt/dotnet",
            "--ro-bind", str(args.runtime / "eval-nuget"), "/nuget",
            "--ro-bind", str(args.server), "/server", "--bind", str(home), "/home/eval",
            "--ro-bind", str(HERE / "inference_relay.py"), "/opt/inference_relay.py",
            "--ro-bind", str(socket_path), "/run/inference.sock",
            "--bind" if edit else "--ro-bind", str(subject), "/subject", *mounts,
            "--setenv", "PATH", "/opt/dotnet:/usr/bin:/bin", "--setenv", "HOME", "/home/eval",
            "--setenv", "CODEX_HOME", "/home/eval", "--setenv", "DOTNET_ROOT", "/opt/dotnet",
            "--setenv", "NUGET_PACKAGES", "/nuget", "--setenv", "DOTNET_CLI_TELEMETRY_OPTOUT", "1",
            "--chdir", "/subject", "python3", "/opt/inference_relay.py", "/opt/codex", "exec",
            "--ephemeral", "--skip-git-repo-check", "--sandbox", "workspace-write" if edit else "read-only",
            "--json", "--output-last-message", "/home/eval/answer.json", "-"]


def profile_config(profile):
    return f"""model = "{MODEL}"
model_provider = "opencode-go"
approval_policy = "never"
web_search = "disabled"
[analytics]
enabled = false
[model_providers.opencode-go]
name = "OpenCode Go"
base_url = "http://127.0.0.1:17563/v1"
wire_api = "responses"
[mcp_servers.dnmcp]
command = "/opt/dotnet/dotnet"
args = ["/server/DotNetMcp.Server.dll", "--roots", "/subject"]
required = true
startup_timeout_sec = 45
tool_timeout_sec = 120
[mcp_servers.dnmcp.env]
DOTNET_ROOT = "/opt/dotnet"
NUGET_PACKAGES = "/nuget"
""" + ('DOTNET_MCP_TOOL_PROFILE = "lite"\n' if profile == "lite" else "")


def trace(events):
    calls, usage, path, patches = {}, [], [], []
    for line in events.read_text(encoding="utf-8").splitlines():
        event = json.loads(line)
        item = event.get("item", {})
        kind = item.get("type")
        if kind in {"command_execution", "file_change", "mcp_tool_call", "dynamic_tool_call", "web_search"}:
            calls[item["id"]] = kind
        if kind == "file_change" and event["type"] == "item.completed" and item["status"] == "completed":
            patches.extend(c["path"] for c in item["changes"] if c["path"].startswith("/subject/"))
        if kind == "mcp_tool_call" and event["type"] == "item.completed":
            name = item["tool"]
            args = item.get("arguments") or {}
            payload = {}
            for block in (item.get("result") or {}).get("content", []):
                try:
                    decoded = json.loads(block.get("text", ""))
                except ValueError:
                    continue
                if isinstance(decoded, dict):
                    payload = decoded
            invocation = {"tool": name, "status": item["status"]}
            if (item.get("error") or {}).get("message") == "MCP tool call requires approval, but approval policy is never":
                invocation["hostApprovalRejected"] = True
            if name == "tool_help":
                invocation["topic"] = args.get("topic")
            if payload.get("error"):
                invocation["error"] = payload["error"]
            if name in {"symbol_preview_rename", "diagnostics_preview_fix", "symbol_preview_refactoring"}:
                invocation["requestedFormat"] = args.get("format", "default")
                if "previewId" in payload:
                    invocation["responseFormat"] = "compact" if "review" in payload else "full"
            if name in {"symbol_apply_rename", "diagnostics_apply_fix", "symbol_apply_refactoring"}:
                invocation["requestedPreviewId"] = args.get("previewId")
            if "previewId" in payload:
                invocation["previewId"] = payload["previewId"]
            if "writtenPaths" in payload:
                invocation["writtenPaths"] = payload["writtenPaths"]
            path.append(invocation)
        if event["type"] == "turn.completed" and event.get("usage"):
            usage.append(event["usage"])
    return dict(inputTokens=sum(u["input_tokens"] for u in usage) if usage else None,
                outputTokens=sum(u["output_tokens"] for u in usage) if usage else None,
                toolCalls=len(calls), mcpCalls=list(calls.values()).count("mcp_tool_call"), mcpPath=path,
                hostPatchPaths=sorted(set(patches)))


def source_digest(files):
    return hashlib.sha256(b"".join(p.encode() + b"\0" + files[p] for p in sorted(files))).hexdigest()


def run_cell(args, key, task_id, rep, snapshot):
    cell = args.out / f"{task_id}-{rep}-{args.profile}"
    cell.mkdir(parents=True, exist_ok=False)  # A started cell is never silently replaced.
    subject, home = cell / "subject", cell / "home"
    tem = {"A1": "A3", "A3": "A3", "D1": "D1", "C1": "C1"}[task_id]
    shutil.copytree(args.runtime / "prepared" / tem, subject)
    if evaluate.files(subject) != snapshot:
        raise ValueError("prepared source differs from pinned task export")
    home.mkdir()
    (home / "config.toml").write_text(profile_config(args.profile))
    prompt = DISCOVERY_PROMPT.encode() if args.discovery else subprocess.check_output(
        [sys.executable, str(HERE / "evaluate.py"), "prompt", task_id])
    events = cell / "events.jsonl"
    edit = "edit" in evaluate.TASKS[task_id]
    with tempfile.TemporaryDirectory() as temp:
        socket_path = Path(temp, "model.sock")
        with model_socket(key, socket_path), events.open("wb") as stdout, (cell / "stderr.log").open("wb") as stderr:
            start = time.monotonic()
            proc = subprocess.Popen(worker(args, subject, home, socket_path, edit), stdin=subprocess.PIPE,
                                    stdout=stdout, stderr=stderr, env={"PATH": "/usr/bin:/bin"}, start_new_session=True)
            try:
                proc.communicate(prompt, timeout=TIMEOUT)
                status = "finished" if proc.returncode == 0 and (home / "answer.json").is_file() else "infra"
            except subprocess.TimeoutExpired:
                os.killpg(proc.pid, signal.SIGKILL)
                proc.wait()
                status = "timeout"
            elapsed = round(time.monotonic() - start, 3)
    row = dict(task="discovery" if args.discovery else task_id, arm=args.profile, rep=rep, commit=args.sha, model=MODEL,
               host=args.host, budgetSeconds=TIMEOUT, status=status, passed=False, seconds=elapsed,
               sourceSha256=source_digest(snapshot), promptSha256=hashlib.sha256(prompt).hexdigest(),
               serverSha256=args.server_digest, **trace(events))
    if status == "finished" and row["inputTokens"] is None:
        row["status"] = "infra"
    if row["status"] == "finished" and args.discovery:
        reached = {call["tool"] for call in row["mcpPath"]}
        expected = {"tool_help", "symbol_attribution", "xaml_resolve_binding", "diagnostics_list_fixes",
                    "symbol_list_refactorings"}
        row.update(passed=expected <= reached,
                   gradeReason="required tools reached" if expected <= reached else "missing: " + str(sorted(expected - reached)))
    elif row["status"] == "finished":
        grade = [sys.executable, str(HERE / "evaluate.py"), "grade", task_id, "--answer", str(home / "answer.json")]
        if edit:
            grade += ["--repo", str(args.repos[task_id]), "--work", str(subject)]
        with (cell / "grader.log").open("wb") as grader_log:
            result = subprocess.run(grade, stdout=subprocess.PIPE, stderr=grader_log)
        try:
            grade = json.loads(result.stdout)
            row.update(passed=grade["passed"], gradeReason=grade["reason"])
            if grade["reason"].startswith("source matched; build exit ") and not grade["passed"]:
                row["status"] = "infra"
        except (ValueError, KeyError):
            row["status"] = "infra"
    after = evaluate.files(subject)
    row["changedFiles"] = sorted(p for p in after.keys() | snapshot.keys() if after.get(p) != snapshot.get(p))
    writes = [call for call in row["mcpPath"] if call.get("writtenPaths")]
    written = {p.removeprefix("/subject/") for call in writes for p in call["writtenPaths"]}
    if writes:
        row["editPath"] = ("mcp+host" if row["hostPatchPaths"] or set(row["changedFiles"]) - written
                           else "mcp-apply")
    else:
        row["editPath"] = "host" if row["changedFiles"] else "none"
    return row


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", choices=("full", "lite"), required=True)
    parser.add_argument("--sha", required=True)
    parser.add_argument("--server", type=Path, required=True, help="extracted package tools/net10.0/any")
    parser.add_argument("--runtime", type=Path, required=True, help="prepared, codex, dotnet, eval-nuget directories")
    parser.add_argument("--avalonia", type=Path, required=True)
    parser.add_argument("--serilog", type=Path, required=True)
    parser.add_argument("--repo", type=Path, required=True, help="git object store with the D1 commit")
    parser.add_argument("--out", type=Path, required=True, help="private operator directory on a Linux filesystem")
    parser.add_argument("--resume", action="store_true", help="skip recorded cells after checking their experiment identity")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--discovery", action="store_true", help="one directed tool discovery probe, outside the natural ledger")
    mode.add_argument("--pilot", action="store_true", help="one separate C1 cell")
    args = parser.parse_args()
    key = os.environ.pop("OpenCodeGo")
    args.host = subprocess.check_output([next((args.runtime / "codex").rglob("bin/codex")), "--version"], text=True).strip()
    args.server_digest = hashlib.sha256((args.server / "DotNetMcp.Server.dll").read_bytes()).hexdigest()
    args.repos = {"A1": args.avalonia, "A3": args.avalonia, "D1": args.repo, "C1": args.serilog}
    os.environ.update(PATH=str(args.runtime / "dotnet") + ":" + os.environ["PATH"],
                      NUGET_PACKAGES=str(args.runtime / "eval-nuget"), RestoreSources=str(args.runtime / "eval-nuget"), NuGetAudit="false", DOTNET_CLI_TELEMETRY_OPTOUT="1")
    args.out.mkdir(parents=True, exist_ok=True)
    ledger = args.out / "cells.jsonl"
    recorded = {}
    if args.resume and ledger.exists():
        for line in ledger.read_text(encoding="utf-8").splitlines():
            row = json.loads(line)
            cell = (row["task"], row["rep"], row["arm"])
            if cell in recorded:
                raise ValueError(f"Duplicate recorded cell {cell}")
            recorded[cell] = row
    for task in ("A1",) if args.discovery else ("C1",) if args.pilot else TASKS:
        with tempfile.TemporaryDirectory() as temp:
            golden = Path(temp, "subject")
            evaluate.prepare(evaluate.TASKS[task], args.repos[task], golden)
            snapshot = evaluate.files(golden)
        for rep in range(1, 2 if args.pilot or args.discovery else 4):
            row = recorded.get(("discovery" if args.discovery else task, rep, args.profile))
            if row is not None:
                prompt = DISCOVERY_PROMPT.encode() if args.discovery else subprocess.check_output(
                    [sys.executable, str(HERE / "evaluate.py"), "prompt", task])
                expected = dict(commit=args.sha, model=MODEL, host=args.host, budgetSeconds=TIMEOUT,
                                sourceSha256=source_digest(snapshot), promptSha256=hashlib.sha256(prompt).hexdigest(),
                                serverSha256=args.server_digest)
                if any(row[key] != value for key, value in expected.items()):
                    raise ValueError(f"Experiment identity changed for {task} repetition {rep}")
                continue
            row = run_cell(args, key, task, rep, snapshot)
            with ledger.open("a", encoding="utf-8") as stream:
                stream.write(json.dumps(row) + "\n")
            print(task, rep, row["status"], row["passed"], row["seconds"], row["mcpCalls"], flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
