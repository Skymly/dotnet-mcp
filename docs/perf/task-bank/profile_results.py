"""Check and summarize a paired, three-repetition workspace-profile ledger."""

import argparse
from collections import Counter
import json
from pathlib import Path
from statistics import median


def summary(rows):
    # Refuse to display a partial/mixed matrix as a paired experiment.
    tasks = ("A1", "A3", "D1", "C1")
    profiles = ("full", "lite")
    expected = {(task, profile, rep) for task in tasks for profile in profiles for rep in (1, 2, 3)}
    actual = {(r["task"], r["arm"], r["rep"]) for r in rows}
    if expected != actual or len(rows) != len(expected):
        raise ValueError("expected exactly three unique repetitions of every task in each profile")
    for key in ("model", "host", "budgetSeconds"):
        if len({r[key] for r in rows}) != 1:
            raise ValueError(f"{key} differs within this experiment")
    for arm in profiles:
        group = [r for r in rows if r["arm"] == arm]
        if len({(r["commit"], r["serverSha256"]) for r in group}) != 1:
            raise ValueError("profile mixes server builds")
    print("| Task | Profile | Correct | Seconds | Input tokens | Output tokens | MCP calls | Zero MCP | Edit path | Infra | Timeout |")
    print("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: |")
    for task in tasks:
        samples = [r for r in rows if r["task"] == task]
        if len({(r["sourceSha256"], r["promptSha256"]) for r in samples}) != 1:
            raise ValueError(f"mismatched source or prompt for {task}")
        for profile in profiles:
            group = [r for r in samples if r["arm"] == profile]
            fields = [task, profile, f"{sum(r['passed'] for r in group)}/{len(group)}"]
            for key in ("seconds", "inputTokens", "outputTokens", "mcpCalls"):
                values = [r[key] for r in group]
                value = None if None in values else median(values)
                fields.append("n/a" if value is None else f"{value:,.1f}" if key == "seconds" else f"{value:,}")
            fields.append(str(sum(r["mcpCalls"] == 0 for r in group)))
            fields.append(", ".join(f"{n} {name}" for name, n in Counter(r["editPath"] for r in group).items()))
            fields.extend(str(sum(r["status"] == status for r in group)) for status in ("infra", "timeout"))
            print("| " + " | ".join(fields) + " |")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("ledger", type=Path)
    args = parser.parse_args()
    summary([json.loads(l) for l in args.ledger.read_text(encoding="utf-8").splitlines() if l.strip()])
