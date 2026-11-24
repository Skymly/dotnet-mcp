"""Exercise named F# references across C#/VB via a packaged stdio server."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

from preview_probe import Client


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--allow-degraded-vb", action="store_true",
                        help="Accept a VB design-time My namespace failure when the bound hit still matches")
    parser.add_argument("command", nargs="+", help="dnmcp or dotnet /absolute/path/to/DotNetMcp.Server.dll")
    args = parser.parse_args()
    os.environ.pop("OpenCodeGo", None)
    fixture = Path(__file__).resolve().parents[3] / "tests/fixtures/FsharpCrossProject"
    with tempfile.TemporaryDirectory(prefix="dnmcp-fsrefs-") as tmp:
        root = Path(tmp)
        shutil.copytree(fixture, root, dirs_exist_ok=True, ignore=shutil.ignore_patterns("bin", "obj"))
        solution = root / "FsharpCrossProject.slnx"
        subprocess.run(["dotnet", "build", str(solution), "-c", "Debug", "-v:q", "--nologo",
                        "--disable-build-servers", "-maxcpucount:1"], check=True, timeout=300)
        with Client(args.command, root) as client:
            client.open(solution)
            projects, _ = client.call("workspace_list_projects")
            producer, = (p for p in projects["projects"] if p["name"] == "FsOnlyCs")
            rows = []
            for name, expected in [("scale", "confirmed"), ("unused", "noHits"), ("factor", "unavailable")]:
                resolved, _ = client.call("symbol_resolve", projectId=producer["projectId"],
                                          name=f"FsOnlyCs.Scaling.{name}")
                query = {"handle": resolved["handle"], "limit": 1}
                coverage, details, sites = {}, {}, []
                while True:
                    page, _ = client.call("symbol_find_references", **query)
                    for project in page.get("projectCoverage", []):
                        coverage[project["name"]] = project["status"]
                        if project.get("reason"):
                            details[project["name"]] = project["reason"]
                    for item in page["items"]:
                        if item.get("projectId") != producer["projectId"]:
                            sites.append(f"{Path(item['filePath']).relative_to(root).as_posix()}:{item['line']}")
                    if "nextCursor" not in page:
                        break
                    query["cursor"] = page["nextCursor"]
                expected_vb = expected
                if args.allow_degraded_vb and expected != "unavailable" and coverage.get("VbConsumer") == "unavailable":
                    vb, = (p for p in projects["projects"] if p["name"] == "VbConsumer")
                    diagnosis, _ = client.call("project_diagnostics", projectId=vb["projectId"])
                    errors = [row for row in diagnosis["items"] if row["severity"] == "Error"]
                    assert errors and all(row["id"] == "BC30002" and "Microsoft.VisualBasic." in row["message"]
                                          for row in errors), errors
                    details["VbConsumer"] += " " + ", ".join(sorted({row["id"] for row in errors}))
                    expected_vb = "unavailable"
                assert coverage == {"CsConsumer": expected, "VbConsumer": expected_vb}, (coverage, details, sites)
                assert sorted(sites) == (["CsConsumer/Use.cs:5", "VbConsumer/Use.vb:3"] if name == "scale" else [])
                rows.append({"symbol": name, "coverage": coverage, "sites": sites, "details": details})
            print(json.dumps(rows, indent=2))


if __name__ == "__main__":
    main()
