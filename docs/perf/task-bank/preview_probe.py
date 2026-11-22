"""Directed A3 stdio acceptance and wire sizes. Run with --help from the controller."""
import argparse
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import tempfile
import threading
import time

import evaluate


class Client:
    """JSON-RPC over the product stdio transport. Sizes include the framing newline."""

    def __init__(self, command, root, profile="full"):
        self.command = command
        self.root = Path(root).resolve()
        self.profile = profile
        self.sequence = 0

    def __enter__(self):
        env = {key: value for key, value in os.environ.items()
               if key.lower() not in {"opencodego", "dotnet_mcp_trusted_roots"}}
        env["DOTNET_MCP_TOOL_PROFILE"] = self.profile
        self.process = subprocess.Popen([*self.command, "--roots", str(self.root)],
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.DEVNULL, env=env)
        self.responses = queue.Queue()

        def read():
            for line in self.process.stdout:
                self.responses.put(line)
            self.responses.put(None)

        self.reader = threading.Thread(target=read, daemon=True)
        self.reader.start()
        try:
            self.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                         "clientInfo": {"name": "dotnet-mcp-preview-probe", "version": "1"}})
            self.send({"method": "notifications/initialized"})
        except BaseException:
            self.__exit__(None, None, None)
            raise
        return self

    def __exit__(self, *_):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
        self.reader.join(timeout=5)
        self.process.stdout.close()

    def send(self, fields):
        frame = json.dumps({"jsonrpc": "2.0", **fields}, separators=(",", ":"))
        self.process.stdin.write(frame.encode("utf-8") + b"\n")
        self.process.stdin.flush()

    def request(self, method, params):
        self.sequence += 1
        self.send({"id": self.sequence, "method": method, "params": params})
        deadline = time.monotonic() + 120
        while True:
            line = self.responses.get(timeout=max(0, deadline - time.monotonic()))
            if line is None:
                raise RuntimeError(f"Server closed stdio during {method}")
            frame = json.loads(line)
            if frame.get("id") != self.sequence:
                continue
            if "error" in frame:
                raise RuntimeError(frame["error"])
            return frame["result"], len(line)

    def call(self, tool, **arguments):
        result, count = self.request("tools/call", {"name": tool, "arguments": arguments})
        payload = json.loads(result["content"][0]["text"])
        if result.get("isError"):
            raise RuntimeError(f"{tool}: {payload}")
        return payload, count

    def open(self, workspace):
        status, _ = self.call("workspace_open", path=str(workspace))
        deadline = time.monotonic() + 120
        while status["phase"] != "ready":
            if status["phase"] in {"failed", "cancelled"} or time.monotonic() > deadline:
                raise RuntimeError(status)
            time.sleep(.25)
            status, _ = self.call("workspace_status")


def review(client, preview):
    page = preview["review"]
    diffs = {}
    extra_bytes = []
    while True:
        for file in page["files"]:
            path = file["path"]
            diff = file["diff"]
            while "nextOffset" in file:
                result, size = client.call("workspace_read_preview", previewId=preview["previewId"],
                                           fileIndex=file["fileIndex"], offset=file["nextOffset"])
                extra_bytes.append(size)
                file, = result["files"]
                diff += file["diff"]
            if path in diffs:
                raise ValueError("Duplicate review path")
            diffs[path] = diff
        if "nextFileIndex" not in page:
            break
        page, size = client.call("workspace_read_preview", previewId=preview["previewId"],
                                 startFileIndex=page["nextFileIndex"])
        extra_bytes.append(size)
    assert len(diffs) == page["fileCount"]
    return diffs, extra_bytes


def run(command, repo, parent, compare_profiles):
    task = evaluate.TASKS["A3"]
    root = parent / "subject"
    evaluate.prepare(task, repo, root)
    workspace = root / evaluate.BANK["subjects"]["avalonia"]["workspace"]
    subprocess.run(["dotnet", "restore", str(workspace), "--nologo", "-v:q"],
                   check=True, stdout=sys.stderr, timeout=300)
    measurements = {}
    for profile in (["full", "lite"] if compare_profiles else ["full"]):
        with Client(command, root, profile) as client:
            tools, size = client.request("tools/list", {})
            measurements[profile] = {"listBytes": size, "toolCount": len(tools["tools"])}
    with Client(command, root) as client:
        client.open(workspace)
        projects, _ = client.call("workspace_list_projects")
        project, = projects["projects"]
        symbol, _ = client.call("symbol_resolve", projectId=project["projectId"],
                                name="CommandSample.ViewModels.CommunityToolkitCommandsViewModel.ConversationLog")
        args = {"handle": symbol["handle"], "newName": task["answer"]["renamed"]}
        full, full_bytes = client.call("symbol_preview_rename", format="full", **args)
        compact, compact_bytes = client.call("symbol_preview_rename", format="compact", **args)
        assert "documents" not in compact and "review" not in full
        assert full["epoch"] == compact["epoch"]
        diffs, extra = review(client, compact)
        assert {Path(path).relative_to(root).as_posix() for path in diffs} == set(task["edit"])
        sites = {}
        for path, unified in diffs.items():
            relative = Path(path).relative_to(root).as_posix()
            expected = task["edit"][relative]
            removed = [line[1:] for line in unified.splitlines() if line.startswith("-")]
            added = [line[1:] for line in unified.splitlines() if line.startswith("+")]
            assert len(removed) == len(added) == len(expected["lines"])
            assert [line.replace(expected["old"], expected["new"], 1) for line in removed] == added
            sites[relative] = len(added)
        applied, _ = client.call("symbol_apply_rename", previewId=compact["previewId"])
        assert set(applied["writtenPaths"]) == set(diffs)
    passed, reason = evaluate.grade(task, {"renamed": compact["newName"]}, repo, root)
    print(json.dumps({"tools": measurements, "fullPreviewBytes": full_bytes,
                      "compactPreviewBytes": compact_bytes, "continuationBytes": extra,
                      "editSites": sites, "passed": passed, "grader": reason}, indent=2))
    return 0 if passed else 1


def main():
    # Local build workers must release the grader's disposable golden copy on Windows.
    os.environ["MSBUILDDISABLENODEREUSE"] = "1"
    os.environ["AVALONIA_TELEMETRY_OPTOUT"] = "1"
    os.environ["UseSharedCompilation"] = "false"
    os.environ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0"
    os.environ.pop("OpenCodeGo", None)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, required=True, help="Avalonia.Samples git checkout containing the pinned revision")
    parser.add_argument("--compare-profiles", action="store_true")
    parser.add_argument("--parent", type=Path, help="Existing empty directory for a retained A3 subject")
    parser.add_argument("command", nargs="+", help="stdio executable and optional arguments, e.g. dotnet /absolute/DotNetMcp.Server.dll")
    args = parser.parse_args()
    if args.parent:
        return run(args.command, args.repo.resolve(), args.parent.resolve(), args.compare_profiles)
    with tempfile.TemporaryDirectory(prefix="dnmcp-preview-") as tmp:
        return run(args.command, args.repo.resolve(), Path(tmp), args.compare_profiles)


if __name__ == "__main__":
    raise SystemExit(main())
