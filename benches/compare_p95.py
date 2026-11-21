"""Join two product bench JSONs by scenario ID. Never count an error/missing row as a speedup."""

import argparse
import csv
import json
from pathlib import Path
import sys


def load(path):
    report = json.loads(path.read_text(encoding="utf-8-sig"))
    rows = {r["id"]: r for r in report["scenarios"]}
    if len(rows) != len(report["scenarios"]):
        raise ValueError(f"duplicate scenario: {path}")
    return report, rows


def error_code(row):
    error = (row or {}).get("error")
    return error if not error or error.isidentifier() else "runtime-error"


def compare(label, old_path, new_path):
    old_report, old = load(old_path)
    new_report, new = load(new_path)
    if old_report["suite"] != new_report["suite"] or old_report["options"].get("symbolName") != new_report["options"].get("symbolName"):
        raise ValueError(f"different suite or probe symbol: {label}")
    def shape(report):
        return {w["name"]: (w["phase"], w["projectCount"]) for w in report["workspaces"]}
    same_graph_size = shape(old_report) == shape(new_report)
    for key in sorted(old.keys() | new.keys()):
        a, b = old.get(key), new.get(key)
        x = a["elapsedMs"]["p95"] if a else None
        y = b["elapsedMs"]["p95"] if b else None
        delta = None
        if not a or not b:
            note = "new" if b else "missing"
        elif a.get("error") or b.get("error"):
            note = "error"
        elif not x or a["iterations"] < 1 or b["iterations"] < 1:
            note = "no-baseline"
        else:
            delta = (y / x - 1) * 100
            note = "over-20%" if y > x * 1.2 else "within-20%"
        yield (label, key, a["iterations"] if a else None, x, b["iterations"] if b else None, y,
               round(delta, 2) if delta is not None else None, note, same_graph_size,
               error_code(a), error_code(b))


if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--pair", nargs=3, metavar=("SUBJECT", "OLD_JSON", "NEW_JSON"), action="append", required=True)
    args = p.parse_args()
    writer = csv.writer(sys.stdout, lineterminator="\n")
    writer.writerow(("subject", "scenario", "old_n", "old_p95_ms", "new_n", "new_p95_ms", "delta_pct",
                     "timing_comparison", "same_workspace_counts", "old_error", "new_error"))
    for name, before, after in args.pair:
        writer.writerows(compare(name, Path(before), Path(after)))
