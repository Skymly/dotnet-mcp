"""Operator-side #528 tools. Never put this directory in a worker's workspace."""

import argparse
from collections import defaultdict
import hashlib
import json
import math
import os
from pathlib import Path
import statistics
import subprocess
import sys
import tarfile
import tempfile


BANK = json.loads(Path(__file__).with_name("bank.json").read_text(encoding="utf-8"))
TASKS = BANK["tasks"]
METRICS = ("seconds", "inputTokens", "outputTokens", "toolCalls")


def transform(root, rules):
    for path, rule in rules.items():
        target = root / path
        data = target.read_bytes()
        if isinstance(rule, dict):
            # Semantic edit sites are known in the pinned revision, not discovered
            # by searching the submission. All bytes outside those sites stay exact.
            old, new = rule["old"].encode(), rule["new"].encode()
            lines = data.splitlines(keepends=True)
            for n in rule["lines"]:
                if not 1 <= n <= len(lines) or lines[n - 1].count(old) != 1:
                    raise ValueError(f"expected one occurrence at pinned edit site: {path}:{n}")
                lines[n - 1] = lines[n - 1].replace(old, new, 1)
            data = b"".join(lines)
        else:
            old, new = (s.encode() for s in rule)
            if old not in data:
                raise ValueError(f"pinned source does not contain the expected token: {path}")
            data = data.replace(old, new)
        target.write_bytes(data)


def prepare(task, repo, root):
    subject = BANK["subjects"][task["subject"]]
    tree = subject["rev"] + (":" + subject["tree"] if subject["tree"] else "")
    root.mkdir(parents=True, exist_ok=False)
    with tempfile.TemporaryFile() as stream:
        subprocess.run(["git", "-C", str(repo), "archive", "--format=tar", tree], stdout=stream, check=True)
        stream.seek(0)
        with tarfile.open(fileobj=stream) as archive:
            archive.extractall(root, filter="data")
    if not (root / subject["workspace"]).is_file():
        raise ValueError("export lacks the specified workspace")
    transform(root, task.get("seed", {}))


def files(root):
    """Compare source bytes, not untrusted build outputs. Never follow links."""
    result = {}
    for parent, dirs, names in os.walk(root):
        dirs[:] = [name for name in dirs if name not in {".git", "bin", "obj"}]
        for name in [*dirs, *names]:
            path = Path(parent, name)
            if path.is_symlink() or os.path.isjunction(path):
                raise ValueError(f"unexpected link: {path.relative_to(root)}")
        for name in names:
            path = Path(parent, name)
            result[path.relative_to(root).as_posix()] = hashlib.sha256(path.read_bytes()).digest()
    return result


def grade(task, answer, repo=None, work=None):
    if answer != task["answer"]:
        return False, "answer mismatch"
    if "edit" not in task:
        return True, "exact answer"
    if not repo or not work or not work.is_dir():
        raise ValueError("edit grading requires --repo and --work (not a self-reported build result)")
    with tempfile.TemporaryDirectory(prefix="dnmcp-grade-") as tmp:
        gold = Path(tmp, "subject")
        prepare(task, repo, gold)
        transform(gold, task["edit"])
        expected, actual = files(gold), files(work)
        wrong = sorted(p for p in expected.keys() | actual.keys() if expected.get(p) != actual.get(p))
        if wrong:
            return False, "unexpected source tree: " + ", ".join(wrong[:8])
        # Build the verified twin, not an agent-modified project file, target or obj/.
        workspace = BANK["subjects"][task["subject"]]["workspace"]
        result = subprocess.run(["dotnet", "build", workspace, "-c", "Release", "--nologo", "-v:q"],
                                cwd=gold, stdout=sys.stderr, timeout=300)
        return result.returncode == 0, f"source matched; build exit {result.returncode}"


def summary(rows):
    cells, groups, identities = set(), defaultdict(list), set()
    for row in rows:
        task, arm, rep = row["task"], row["arm"], row["rep"]
        if task not in TASKS or arm not in ("control", "tool") or type(rep) is not int or not 1 <= rep <= 3:
            raise ValueError("unknown task, arm or repetition")
        if (task, arm, rep) in cells:
            raise ValueError("duplicate attempt; never replace a failed run")
        if row["status"] not in ("finished", "timeout", "infra") or type(row["passed"]) is not bool:
            raise ValueError("invalid status or grade")
        if row["status"] != "finished" and row["passed"]:
            raise ValueError("timeout/infrastructure failure cannot pass")
        for key in METRICS:
            value = row[key]
            if value is None and row["status"] != "finished":
                continue
            if type(value) not in (int, float) or not math.isfinite(value) or value < 0:
                raise ValueError(f"invalid {key}")
        identity = tuple(row[k] for k in ("model", "host", "candidate", "budgetSeconds"))
        if not all(isinstance(x, str) and x.strip() for x in identity[:3]):
            raise ValueError("missing experiment identity")
        if type(row["budgetSeconds"]) is not int or row["budgetSeconds"] <= 0:
            raise ValueError("invalid wall-clock budget")
        if row["passed"] and row["seconds"] > row["budgetSeconds"]:
            raise ValueError("over-budget run cannot pass")
        identities.add(identity)
        cells.add((task, arm, rep))
        groups[TASKS[task]["category"], arm].append(row)

    complete = len(cells) == len(TASKS) * 2 * 3
    gate = complete and len(identities) == 1 and not any(r["status"] == "infra" for r in rows)
    categories = {}
    for category in sorted({t["category"] for t in TASKS.values()}):
        arms = {}
        for arm in ("control", "tool"):
            attempts = groups[category, arm]
            medians = {}
            for key in METRICS:
                values = [r[key] for r in attempts if r[key] is not None]
                medians[key] = statistics.median(values) if len(values) == len(attempts) and values else None
            arms[arm] = {"passed": sum(r["passed"] for r in attempts), "n": len(attempts), "median": medians}
        gate = gate and arms["tool"]["passed"] > 0 and arms["tool"]["passed"] >= arms["control"]["passed"]
        categories[category] = arms
    return {"attempts": len(cells), "complete": complete, "gate": gate, "categories": categories}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    for command in ("prepare", "prompt", "grade"):
        p = commands.add_parser(command)
        p.add_argument("task", choices=TASKS)
        if command in ("prepare", "grade"):
            p.add_argument("--repo", type=Path, required=command == "prepare")
            p.add_argument("--work", type=Path, required=command == "prepare")
        if command == "grade":
            p.add_argument("--answer", type=Path, required=True)
    commands.add_parser("summary").add_argument("ledger", type=Path)
    args = parser.parse_args()
    if args.command == "summary":
        rows = [json.loads(line) for line in args.ledger.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
        result = summary(rows)
        print(json.dumps(result, indent=2))
        return 0 if result["gate"] else 1
    task = TASKS[args.task]
    if args.command == "prepare":
        prepare(task, args.repo, args.work)
    elif args.command == "prompt":
        print("Workspace:", BANK["subjects"][task["subject"]]["workspace"])
        print(task["prompt"])
        print("Return only a JSON object. " + ("Apply the edit to this copy." if "edit" in task else "Do not edit files."))
    else:
        try:
            answer = json.loads(args.answer.read_text(encoding="utf-8-sig"))
        except (FileNotFoundError, json.JSONDecodeError):
            answer = None
        passed, reason = grade(task, answer, args.repo, args.work)
        print(json.dumps({"task": args.task, "passed": passed, "reason": reason}))
        return 0 if passed else 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
