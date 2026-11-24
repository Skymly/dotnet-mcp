"""Prepare or verify the general worker templates used by profile_trial.py (operator-side)."""

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

import evaluate


def run():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--runtime", type=Path, required=True)
    p.add_argument("--avalonia", type=Path, required=True)
    p.add_argument("--serilog", type=Path, required=True)
    p.add_argument("--repo", type=Path, required=True)
    p.add_argument("--hydrate", action="store_true", help="allow trusted template restore from nuget.org")
    args = p.parse_args()
    os.environ.pop("OpenCodeGo", None)
    prepared = args.runtime / "prepared"
    packages = args.runtime / "eval-nuget"
    packages.mkdir(exist_ok=True)
    for task, repo in (("A3", args.avalonia), ("D1", args.repo), ("C1", args.serilog)):
        info = evaluate.TASKS[task]
        project = evaluate.BANK["subjects"][info["subject"]]["workspace"]
        template = prepared / task
        with tempfile.TemporaryDirectory() as tmp:
            copy = Path(tmp, "subject")
            evaluate.prepare(info, repo, copy)
            if template.exists():
                if evaluate.files(copy) != evaluate.files(template):
                    raise ValueError(f"{task} template differs from the pinned input")
                shutil.copytree(template, copy, dirs_exist_ok=True)
            args_build = (["restore", project] if task == "C1" else
                          ["build", project, "-c", "Release", "-t:Rebuild", "-m:1", "-p:UseSharedCompilation=false"])
            if template.exists() and task != "C1":
                args_build.append("--no-restore")
            if template.exists() and task == "C1":
                if not (copy / Path(project).parent / "obj/project.assets.json").is_file():
                    raise RuntimeError("C1 restore template lacks assets")
                print(task, "OK (pinned sources and restored assets)", flush=True)
                continue
            # These are pinned trusted repositories. Workers later see a read-only package cache.
            restore_source = "https://api.nuget.org/v3/index.json" if args.hydrate else "/nuget"
            config = Path(tmp, "NuGet.Config")
            config.write_text('<configuration><packageSources><clear/><add key="packages" value="' +
                              restore_source + '"/></packageSources></configuration>')
            build_env = {"PATH": "/usr/bin:/bin"}
            if args.hydrate:
                for name in ("https_proxy", "http_proxy", "no_proxy", "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY"):
                    if name in os.environ:
                        build_env[name] = os.environ[name]
            command = ["bwrap", "--unshare-all", *(["--share-net"] if args.hydrate else []), "--die-with-parent",
                       "--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp", "--ro-bind", "/usr", "/usr",
                       "--symlink", "usr/bin", "/bin", "--symlink", "usr/lib", "/lib",
                       "--symlink", "usr/lib64", "/lib64", "--ro-bind", "/etc/ssl", "/etc/ssl",
                       "--ro-bind", "/etc/resolv.conf", "/etc/resolv.conf", "--ro-bind", "/etc/passwd", "/etc/passwd",
                       "--ro-bind", "/etc/group", "/etc/group", "--ro-bind", str(config), "/home/eval/NuGet.Config",
                       "--ro-bind", str(args.runtime / "dotnet"), "/opt/dotnet",
                       "--bind", str(copy), "/subject", "--bind", str(packages), "/nuget",
                       "--setenv", "HOME", "/home/eval", "--setenv", "PATH", "/opt/dotnet:/usr/bin:/bin",
                       "--setenv", "DOTNET_ROOT", "/opt/dotnet", "--setenv", "NUGET_PACKAGES", "/nuget",
                       "--setenv", "DOTNET_CLI_TELEMETRY_OPTOUT", "1", "--setenv", "RestoreSources", restore_source, "--setenv", "NuGetAudit", "false",
                       "--setenv", "RestoreConfigFile", "/home/eval/NuGet.Config", "--chdir", "/subject"]
            result = subprocess.run([*command, "dotnet", *args_build], capture_output=True, text=True, timeout=300, env=build_env)
            if task == "D1":
                if result.returncode != 1 or "error CS0117" not in result.stdout:
                    raise RuntimeError("seeded D1 must produce CS0117: " + result.stdout[-1600:])
                if not template.exists():
                    shutil.copytree(copy, template)
                evaluate.transform(copy, info["edit"])
                subprocess.run([*command, "dotnet", *args_build], check=True, stdout=subprocess.DEVNULL, env=build_env)
            elif result.returncode:
                raise RuntimeError(f"{task} preflight failed: {result.stdout[-1000:]}")
            elif not template.exists():
                shutil.copytree(copy, template)
        print(task, "OK", flush=True)


if __name__ == "__main__":
    run()
