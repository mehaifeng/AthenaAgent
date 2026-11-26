#!/usr/bin/env python3
"""Build and exercise the normal Athena window with disposable local conversations."""
import argparse
import json
import ctypes
from datetime import datetime, timezone
import hashlib
import os
from pathlib import Path
import platform
import sys
import shutil
import sqlite3
import subprocess
import tempfile
import time
import uuid
import wave

ROOT = Path(__file__).resolve().parents[1]
TURNS = 160
MODELS = 2400
CATALOG_SIZE = 400
RICH = """## Measurement

| Sample | Score |
| --- | ---: |
| **First** | 27 |
| *Second* | 83 |

> Review the `latency` and [source](https://example.org).

```csharp
public record Sample(double Duration);
```

- [x] Inspect the trace
- [ ] Compare $x^2 + y^2$
"""


def seed(data):
    data.mkdir()
    shutil.copytree(ROOT / "BuiltInSkills", data / "Skills", ignore=shutil.ignore_patterns("bin", "obj"))
    knowledge = data / "KnowledgeBase"
    knowledge.mkdir()
    shutil.copyfile(ROOT / "Assets/AthenaSoul.md", knowledge / "AthenaSoul.md")
    # Use a fresh, valid local LKG so that neither a remote refresh nor the network can
    # change the amount of work between two binaries. Some models have no catalogue match.
    catalog = data / "ModelMetadata/OpenRouter"
    (catalog / "snapshots").mkdir(parents=True)
    reference = [dict(id=f"bench/model-{i:04d}", canonicalSlug=None, name=f"Model {i}", createdUnixSeconds=None,
                      description=None, contextLength=100_000, architecture=dict(inputModalities=["text", "image"],
                      outputModalities=["text"], tokenizer=None, instructType=None), topProvider=None, pricing=None,
                      supportedParameters=["tools"], defaultParameters=None, expirationDate=None, raw=None)
                 for i in range(CATALOG_SIZE)]
    digest = hashlib.sha256(json.dumps(reference, indent=2).encode()).hexdigest()
    now = datetime.now(timezone.utc).isoformat()
    (catalog / "snapshots" / (digest + ".json")).write_text(json.dumps(dict(schemaVersion=1, catalogRevision=digest,
        fetchedAtUtc=now, sourceUrl="https://example.org/models", contentHash=digest, eTag=None, models=reference)))
    (catalog / "current.json").write_text(json.dumps(dict(schemaVersion=1, currentRevision=digest,
        previousRevision=None, eTag=None, lastCheckedAtUtc=now)))
    media = data / "Attachments"
    media.mkdir()
    shutil.copyfile(ROOT / "Assets/EmptyConversationLogo.png", media / "picture.png")
    with wave.open(str(media / "sample.wav"), "wb") as output:
        output.setparams((1, 2, 16000, 0, "NONE", ""))
        output.writeframes(bytes(32000))
    config = {"configSchemaVersion": 8, "onboardingCompleted": True, "aiModels": {
        "providers": [{"id": "bench", "displayName": "Benchmark", "baseUrl": "http://127.0.0.1:1/v1",
                       "models": [{"id": f"model-{i:04d}", "capability": 1} for i in range(MODELS)]}],
        "mainConversation": {"providerId": "bench", "model": "model-0000"},
        "computerUse": {"providerId": "bench", "model": "model-0001"}}}
    (data / "config.json").write_text(json.dumps(config))
    with sqlite3.connect(data / "conversations.db") as db:
        # Original shipped schema. This also exercises the upgrade path with real payloads.
        db.execute("""CREATE TABLE conversations(id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL,
            workspace_id TEXT, parent_conversation_id TEXT, title TEXT NOT NULL, created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL, revision INTEGER NOT NULL DEFAULT 0, payload_hash TEXT, payload TEXT NOT NULL)""")
        for session in range(100):
            ident = str(uuid.UUID(int=session + 1))
            title = f"Benchmark {session:03d}"
            date = f"2026-10-{28 if session == 0 else 27 if session == 1 else 1:02d}T12:00:00"
            messages = []

            def message(role, content, **fields):
                row = dict(id=f"{session}-{len(messages)}", role=role, content=content, **fields)
                messages.append(row)
                return row

            turns = TURNS if session < 2 else 10
            for turn in range(turns):
                message("user", f"Question {turn + 1} — review the samples.")
                content = f"Answer {turn + 1}\n\n{RICH}"
                if turn % 40 == 38:
                    content += ("\n\n" + "The instrument records each sample. " * 20) * 36
                segments = []
                attachments = []
                if turn % 4 == 3:
                    calls = [dict(toolCallId=f"tool-{turn}-{i}", name="read_file", status=1,
                                  summary=f"Read section {i}", arguments='{"path":"sample.md"}',
                                  result="Measurement " * 80) for i in range(16)]
                    message("assistant", "", isHidden=True, toolCallsJson=json.dumps([
                        {"id": c["toolCallId"], "type": "function", "function": {
                            "name": c["name"], "arguments": c["arguments"]}} for c in calls]))
                    for call in calls:
                        message("tool", call["result"], toolCallId=call["toolCallId"])
                    segments.extend([dict(kind=3, text="Check the measurements."),
                                     dict(kind=2, toolCalls=calls)])
                if turn % 10 == 9:
                    for name, kind, mime in [("picture.png", 1, "image/png"), ("sample.wav", 4, "audio/wav")]:
                        path = media / name
                        attachments.append(dict(id=f"{session}-{turn}-{kind}", kind=kind, fileName=name,
                                                mimeType=mime, storedPath=str(path), sizeBytes=path.stat().st_size))
                    segments.append(dict(kind=1, attachmentId=attachments[0]["id"]))
                if turn % 20 == 19:
                    content += "\n\n```mermaid\nflowchart LR\n  Input --> Output\n```"
                segments.append(dict(kind=0, text=content))
                message("assistant", content, segments=segments, attachments=attachments)
            payload = dict(id=ident, conversationId=ident, summary=title, createdAt=date, updatedAt=date,
                           revision=1, messages=messages, messageCount=2 * turns)
            db.execute("INSERT INTO conversations VALUES (?,?,NULL,NULL,?,?,?,1,NULL,?)",
                       (ident, ident, title, date, date, json.dumps(payload, separators=(",", ":"))))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runs", type=int, default=3)
    parser.add_argument("--no-build", action="store_true")
    parser.add_argument("--app-dir", type=Path, help="Test a self-contained release publish directory")
    parser.add_argument("--fresh-install", action="store_true", help="Copy all executable files to new inodes before the first launch")
    parser.add_argument("--hz", type=float, help="Refresh rate of the display containing the test window")
    parser.add_argument("--baseline", action="store_true", help="Report results without enforcing targets")
    parser.add_argument("--startup-only", action="store_true", help="Stop after the selected rich reply has rendered")
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    project = ROOT / "Tools/DesktopPerformance"
    binaries = args.app_dir
    if binaries is None:
        system = {"darwin": "osx", "linux": "linux", "win32": "win"}.get(sys.platform)
        arch = {"arm64": "arm64", "aarch64": "arm64", "x86_64": "x64", "amd64": "x64"}.get(platform.machine().lower())
        if not system or not arch:
            parser.error("Supply the release publish directory for this platform with --app-dir")
        rid = f"{system}-{arch}"
        binaries = project / "bin" / "desktop-publish" / rid
        if not args.no_build:
            subprocess.run(["dotnet", "publish", str(ROOT / "Athena.UI.csproj"), "-c", "Release",
                            "-r", rid, "--self-contained", "true", "-p:PublishSingleFile=false",
                            "-o", str(binaries)], check=True)
    if not args.no_build:
        subprocess.run(["dotnet", "build", str(project), "-c", "Release", "-p:UseAppHost=false", "-v:q"], check=True)
    hz = args.hz
    if hz is None and sys.platform == "darwin":
        cg = ctypes.CDLL("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")
        cg.CGDisplayCopyDisplayMode.argtypes = [ctypes.c_uint32]
        cg.CGDisplayCopyDisplayMode.restype = ctypes.c_void_p
        cg.CGDisplayModeGetRefreshRate.argtypes = [ctypes.c_void_p]
        cg.CGDisplayModeGetRefreshRate.restype = ctypes.c_double
        hz = cg.CGDisplayModeGetRefreshRate(cg.CGDisplayCopyDisplayMode(cg.CGMainDisplayID()))
    if not hz or hz <= 0 or args.runs < 1:
        parser.error("Specify a positive display refresh rate with --hz and at least one run")
    results = []
    with tempfile.TemporaryDirectory(prefix="athena-desktop-") as tmp:
        base = Path(tmp).resolve()
        for item in binaries.iterdir():
            if item.name == "AthenaData":
                continue
            if item.is_dir():
                (base / item.name).symlink_to(item.resolve(), target_is_directory=True)
            elif args.fresh_install:
                shutil.copy2(item, base / item.name)
            else:
                # Preserve the installed runtime's file identity, including OS code-signature
                # caches. Only the app's writable data is new for every process launch.
                try:
                    os.link(item, base / item.name)
                except OSError:
                    shutil.copy2(item, base / item.name)
        for attempt in range(args.runs):
            data = base / "AthenaData"
            seed(data)
            hook = project / "bin/Release/net10.0/Athena.DesktopPerformance.dll"
            env = dict(os.environ, ATHENA_DESKTOP_BENCHMARK=str(base), DOTNET_STARTUP_HOOKS=str(hook))
            env["ATHENA_BENCHMARK_STARTUP_ONLY"] = "1" if args.startup_only else "0"
            command = [str(base / ("Athena.UI.exe" if os.name == "nt" else "Athena.UI"))]
            env["ATHENA_BENCHMARK_LAUNCH_MS"] = str(time.time_ns() // 1_000_000)
            proc = subprocess.run(command, env=env,
                                  stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=360)
            if os.environ.get("ATHENA_PROFILE_TIMES"):
                print("Parent launch UTC milliseconds", env["ATHENA_BENCHMARK_LAUNCH_MS"])
                print((data / "Logs/crash.log").read_text()[:900])
                with sqlite3.connect(data / "Logs/logs.db") as logs:
                    for stamp, message in logs.execute("SELECT Timestamp,Message FROM Logs ORDER BY Id LIMIT 180"):
                        print(stamp, message[:125])
            for line in proc.stdout.splitlines():
                if line.startswith("DESKTOP "):
                    print(line, flush=True)
            if proc.returncode or not (base / "result.json").exists():
                raise RuntimeError(proc.stdout[-5000:])
            run = json.loads((base / "result.json").read_text())
            run["refreshHz"] = hz
            run["launchMode"] = ("fresh-copy-first-open" if args.fresh_install else "installed-files-new-path") if attempt == 0 else "installed-path-new-process"
            results.append(run)
            if args.report:
                args.report.write_text(json.dumps(results, indent=2) + "\n")
            (base / "result.json").unlink()
            shutil.rmtree(data)
    if not args.baseline:
        for run in results:
            frames = ("scrollFrames", "middleFrames", "resize", "rightResize", "modelFrames")
            if run["startupMs"] >= 3000 or not args.startup_only and (run["switchMs"] >= 3000 or any(
                    run[name]["fps"] < .95 * hz or not run[name]["sufficientMovement"] for name in frames)):
                raise SystemExit("Desktop performance target missed")


if __name__ == "__main__":
    main()
