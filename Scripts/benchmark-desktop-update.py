#!/usr/bin/env python3
"""Measure a real macOS updater installing a new release over a used, disposable installation."""
import argparse
import json
import os
from pathlib import Path
import plistlib
import runpy
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
seed = runpy.run_path(str(ROOT / "Scripts/benchmark-desktop.py"))["seed"]


def run(command, env):
    with subprocess.Popen(command, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True) as process:
        try:
            output, _ = process.communicate(timeout=180)
        except subprocess.TimeoutExpired:
            process.kill()
            process.communicate()
            raise
        if process.returncode:
            raise RuntimeError(output[-5000:])
        return process.pid


def measure(previous, replacement, updater, hook):
    with tempfile.TemporaryDirectory(prefix="athena-used-install-") as tmp:
        root = Path(tmp).resolve()
        contents = root / "Athena.app/Contents"
        install = contents / "MacOS"
        # An update may overwrite its destination. Never share writable files with the
        # build outputs or a user's installation, including through directory symlinks.
        shutil.copytree(previous, install, ignore=shutil.ignore_patterns("AthenaData"))
        plist = contents / "Info.plist"
        plist.write_bytes(plistlib.dumps({"CFBundleExecutable": "Athena.UI",
            "CFBundleVersion": "0.0.1", "CFBundleShortVersionString": "0.0.1"}))
        data = install / "AthenaData"
        seed(data)
        result = install / "result.json"
        env = dict(os.environ, DOTNET_STARTUP_HOOKS=str(hook), ATHENA_DESKTOP_BENCHMARK=str(install),
                   ATHENA_BENCHMARK_STARTUP_ONLY="1")
        previous_pid = run([str(install / "Athena.UI")],
                           dict(env, ATHENA_BENCHMARK_LAUNCH_MS=str(time.time_ns() // 1_000_000)))
        previous_startup = json.loads(result.read_text())["startupMs"]
        result.unlink()
        session = root / "update.json"
        session.write_text(json.dumps(dict(mainProcessId=previous_pid, installDirectory=str(install),
            stagingDirectory=str(replacement), entryExecutable="Athena.UI", version="0.0.2",
            preservePaths=["AthenaData", "updater"])))
        start = time.monotonic()
        run([str(updater), "--session", str(session)], dict(env, ATHENA_BENCHMARK_UPDATER=str(updater)))
        while not result.exists():
            if time.monotonic() - start > 120:
                raise TimeoutError("The updated application did not render its archived reply")
            time.sleep(.02)
        updated = json.loads(result.read_text())
        if plistlib.loads(plist.read_bytes())["CFBundleShortVersionString"] != "0.0.2":
            raise RuntimeError("The updater did not apply the new version to the bundle")
        with sqlite3.connect(data / "conversations.db") as db:
            if db.execute("SELECT count(*) FROM conversations").fetchone()[0] != 100:
                raise RuntimeError("The update did not preserve the existing archives")
        return dict(previousStartupMs=previous_startup, updatedStartupMs=updated["startupMs"],
                    updatedWindowMs=updated["openedMs"], updateAndRelaunchMs=round((time.monotonic() - start) * 1000))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--previous", type=Path, required=True)
    parser.add_argument("--app-dir", type=Path, required=True)
    parser.add_argument("--updater", type=Path, required=True)
    parser.add_argument("--runs", type=int, default=3)
    parser.add_argument("--baseline", action="store_true", help="Report the original updater without enforcing the gate")
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    if sys.platform != "darwin" or args.runs < 1:
        parser.error("Use a macOS desktop and a positive run count")
    hook = ROOT / "Tools/DesktopPerformance/bin/Release/net10.0/Athena.DesktopPerformance.dll"
    rows = []
    for _ in range(args.runs):
        row = measure(args.previous.resolve(), args.app_dir.resolve(), args.updater.resolve(), hook)
        print(json.dumps(row), flush=True)
        rows.append(row)
        if args.report:
            args.report.write_text(json.dumps(rows, indent=2) + "\n")
    if not args.baseline and any(row["updatedStartupMs"] >= 3000 for row in rows):
        raise SystemExit("Updated process cold startup exceeded the target")


if __name__ == "__main__":
    main()
