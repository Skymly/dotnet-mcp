"""Directed D1 acceptance using the lite MCP catalog, its default preview, and the pinned grader."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile

import evaluate
from preview_probe import Client, review


class TracedClient(Client):
    def request(self, method, params):
        name = params["name"] if method == "tools/call" else method
        result, size = super().request(method, params)
        self.trace.append({"call": name, "responseBytes": size})
        return result, size


def run(command, repo, parent):
    task = evaluate.TASKS["D1"]
    root = parent / "subject"
    evaluate.prepare(task, repo, root)
    workspace = root / evaluate.BANK["subjects"][task["subject"]]["workspace"]
    seeded = subprocess.run(["dotnet", "build", str(workspace), "-c", "Release", "--nologo", "-v:q"],
                            capture_output=True, timeout=300)
    if seeded.returncode == 0 or b"CS0117" not in seeded.stdout + seeded.stderr:
        raise RuntimeError("The pinned D1 seed did not produce the expected compiler error.")

    client = TracedClient(command, root, "lite")
    client.trace = []
    with client:
        catalog, _ = client.request("tools/list", {})
        registered = {tool["name"] for tool in catalog["tools"]}
        groups, _ = client.call("tool_help")
        assert "diagnostics" in groups
        group, _ = client.call("tool_help", topic="diagnostics")
        discovered = {tool["name"] for tool in group}
        workflow = {"project_diagnostics", "diagnostics_list_fixes", "diagnostics_preview_fix", "diagnostics_apply_fix"}
        assert workflow <= discovered <= registered

        client.open(workspace)
        projects, _ = client.call("workspace_list_projects")
        project_id, = [p["projectId"] for p in projects["projects"] if p["name"] == "App"]
        errors, _ = client.call("project_diagnostics", projectId=project_id)
        diagnostic, = [row for row in errors["items"] if row["id"] == "CS0117"]
        path = Path(diagnostic["filePath"])
        relative = path.relative_to(root).as_posix()
        assert relative in task["edit"]
        locator = {key: diagnostic[key] for key in ("filePath", "line", "column", "endLine", "endColumn")}
        locator.update(projectId=project_id, diagnosticId=diagnostic["id"])
        fixes, _ = client.call("diagnostics_list_fixes", **locator)
        alternatives = [{"fixIndex": fix["fixIndex"], "title": fix["title"], "equivalenceKey": fix.get("equivalenceKey")}
                        for fix in fixes["items"]]
        # Titles are localized. Roslyn's spelling correction uses the target identifier as its key.
        old, new = task["edit"][relative]
        matches = [fix for fix in alternatives if fix["equivalenceKey"] == new.split(".")[-1]]
        if len(matches) != 1:
            print(json.dumps({"task": "D1", "profile": "lite", "passed": False, "fixes": alternatives,
                              "reason": "No unique spelling correction matched the pinned target.", "trace": client.trace}))
            return 1
        spelling, = matches
        original = path.read_bytes()
        preview, size = client.call("diagnostics_preview_fix", fixIndex=spelling["fixIndex"], **locator)
        assert preview["epoch"] == fixes["epoch"] and "documents" not in preview
        diffs, pages = review(client, preview)
        (changed_path, diff), = diffs.items()
        assert Path(changed_path) == path and path.read_bytes() == original
        removed, = [line[1:] for line in diff.splitlines() if line.startswith("-")]
        added, = [line[1:] for line in diff.splitlines() if line.startswith("+")]
        assert old in removed and added == removed.replace(old, new, 1)
        applied, _ = client.call("diagnostics_apply_fix", previewId=preview["previewId"])
        assert [Path(p) for p in applied["writtenPaths"]] == [path]
        after, _ = client.call("project_diagnostics", projectId=project_id)
        assert not any(row["id"] == diagnostic["id"] for row in after["items"])
    passed, reason = evaluate.grade(task, {"diagnostic": diagnostic["id"]}, repo, root)
    print(json.dumps({"task": "D1", "profile": "lite", "path": relative, "line": diagnostic["line"],
                      "column": diagnostic["column"], "endColumn": diagnostic["endColumn"], "diagnostic": diagnostic["id"],
                      "fixes": alternatives, "appliedIndex": spelling["fixIndex"], "compactPreviewBytes": size,
                      "continuationBytes": pages, "trace": client.trace, "passed": passed, "grader": reason}, indent=2))
    return 0 if passed else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, required=True, help="dotnet-mcp checkout containing the pinned D1 fixture")
    parser.add_argument("--parent", type=Path, help="Empty directory to retain the prepared source tree")
    parser.add_argument("command", nargs="+", help="stdio command, e.g. dotnet /absolute/path/DotNetMcp.Server.dll")
    args = parser.parse_args()
    # This directed probe uses no model. Children, including the build inside the grader,
    # need only their toolchain configuration.
    for name in list(os.environ):
        if name.lower() == "opencodego":
            del os.environ[name]
    if args.parent:
        return run(args.command, args.repo.resolve(), args.parent.resolve())
    with tempfile.TemporaryDirectory(prefix="dnmcp-diagnostic-") as tmp:
        return run(args.command, args.repo.resolve(), Path(tmp))


if __name__ == "__main__":
    raise SystemExit(main())
