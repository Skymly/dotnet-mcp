"""Run ONE ungraded Codex/Ollama cell. This worker does not read the bank or oracle."""

import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time
from urllib.request import ProxyHandler, build_opener


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--codex", required=True, help="native Codex CLI executable")
    p.add_argument("--model", required=True, help="already-installed local Ollama tag")
    p.add_argument("--subject", type=Path, required=True)
    p.add_argument("--out", type=Path, required=True, help="new controller directory outside the subject")
    p.add_argument("--server", type=Path, help="tool arm only: Release DotNetMcp.Server.dll")
    p.add_argument("--edit", action="store_true", help="use workspace-write, only for edit tasks")
    p.add_argument("--timeout", type=int, default=1200)
    args = p.parse_args()
    root = args.subject.resolve(strict=True)
    out = args.out.resolve()
    if out.is_relative_to(root) or not root.is_dir() or args.timeout <= 0:
        p.error("need a subject directory, separate output directory and positive timeout")
    prompt = sys.stdin.buffer.read()
    if not prompt.strip():
        p.error("supply only the neutral task prompt on stdin")
    api = "http://127.0.0.1:11434"
    with build_opener(ProxyHandler({})).open(api + "/api/tags", timeout=5) as response:
        models = {m["name"]: m["digest"] for m in json.load(response)["models"]}
    if args.model not in models:
        p.error("model is not installed on loopback Ollama; will not download or fall back")
    out.mkdir(exist_ok=False)
    home = out / "codex-home"
    home.mkdir()
    # Also pin the app server's active provider; --oss alone selects the thread
    # provider but a fresh Codex 0.166 home otherwise defaults to OpenAI on turn/start.
    config = 'model_provider = "ollama"\napproval_policy = "never"\n[analytics]\nenabled = false\n'
    if args.server:
        config += '\n[mcp_servers.dnmcp]\ncommand = "dotnet"\nrequired = true\nargs = '
        config += json.dumps([str(args.server.resolve(strict=True)), "--roots", str(root)]) + "\n"
    (home / "config.toml").write_text(config, encoding="utf-8")
    # No hosted-provider credentials, inherited MCP configuration or proxy.
    keep = {"PATH", "PATHEXT", "SYSTEMROOT", "WINDIR", "COMSPEC", "TEMP", "TMP", "TMPDIR",
            "USERPROFILE", "HOME", "LOCALAPPDATA", "APPDATA", "PROGRAMDATA", "PROGRAMFILES",
            "PROGRAMFILES(X86)", "DOTNET_ROOT", "NUGET_PACKAGES", "LANG", "LC_ALL"}
    env = {k: v for k, v in os.environ.items() if k.upper() in keep}
    env.update(CODEX_HOME=str(home), OLLAMA_HOST=api, CODEX_OSS_BASE_URL=api + "/v1",
               DOTNET_CLI_TELEMETRY_OPTOUT="1")
    last, events = out / "answer.json", out / "events.jsonl"
    command = [args.codex, "exec", "--oss", "--local-provider", "ollama", "-m", args.model,
               "--ephemeral", "--skip-git-repo-check", "--sandbox",
               "workspace-write" if args.edit else "read-only", "-C", str(root),
               "--json", "--output-last-message", str(last), "-"]
    start = time.monotonic()
    with events.open("wb") as stdout, (out / "stderr.log").open("wb") as stderr:
        proc = subprocess.Popen(command, cwd=root, env=env, stdin=subprocess.PIPE, stdout=stdout,
                                stderr=stderr, start_new_session=os.name != "nt")
        try:
            proc.communicate(prompt, timeout=args.timeout)
            status = "finished" if proc.returncode == 0 and last.is_file() else "infra"
        except subprocess.TimeoutExpired:
            status = "timeout"
            if os.name == "nt":
                subprocess.run(["taskkill", "/PID", str(proc.pid), "/T", "/F"], stdout=stderr, stderr=stderr)
            else:
                os.killpg(proc.pid, signal.SIGKILL)
            proc.wait(timeout=15)
    seconds = round(time.monotonic() - start, 3)
    calls, mcp, usages = set(), set(), []
    for line in events.read_text(encoding="utf-8", errors="replace").splitlines():
        try:
            event = json.loads(line)
        except json.JSONDecodeError:
            continue
        item = event.get("item", {})
        kind = item.get("type")
        if kind in {"command_execution", "file_change", "mcp_tool_call", "web_search", "dynamic_tool_call"}:
            calls.add(item["id"])
            if kind == "mcp_tool_call":
                mcp.add(item["id"])
        if event.get("type") == "turn.completed" and event.get("usage"):
            usages.append(event["usage"])
    if status == "finished" and (not usages or seconds > args.timeout):
        status = "infra" if not usages else "timeout"
    complete_usage = bool(usages) and status == "finished"
    receipt = dict(model=args.model, modelDigest=models[args.model], arm="tool" if args.server else "control",
                   budgetSeconds=args.timeout, status=status, seconds=seconds,
                   inputTokens=sum(u["input_tokens"] for u in usages) if complete_usage else None,
                   outputTokens=sum(u["output_tokens"] for u in usages) if complete_usage else None,
                   toolCalls=len(calls), mcpCalls=len(mcp))
    text = json.dumps(receipt, indent=2)
    (out / "receipt.json").write_text(text + "\n", encoding="utf-8")
    print(text)
    return 0 if status == "finished" else 1


if __name__ == "__main__":
    raise SystemExit(main())
