#!/usr/bin/env python3
"""Measure the Avalonia browser pane on Linux or macOS with local fixtures or live TestUFO."""
import argparse
from contextlib import contextmanager
import http.server
import json
import os
import platform
from pathlib import Path
import statistics
import subprocess
import tempfile
import threading


ROOT = Path(__file__).resolve().parents[1]
LINUX = platform.system() == "Linux"
MODES = ("bitmap-default", "bitmap-egl", "dmabuf-egl") if LINUX else ("bitmap-opengl", "bitmap-metal", "iosurface-metal")
SCROLL = b"""<!doctype html><meta charset=utf-8><title>Browser scrolling fixture</title>
<style>
html { background:#f5f6f7; color:#131d2a; font:16px system-ui }
main { display:grid; grid-template-columns:repeat(2,1fr); gap:9px; padding:10px }
article { background:#fff; box-shadow:0 1px 3px #0003; padding:12px; border-radius:10px }
b { display:block; height:78px; border-radius:5px }
</style><main></main><script>
document.querySelector('main').innerHTML=Array.from({length:720}, (_, i) =>
  `<article><b style="background:linear-gradient(135deg,hsl(${i%360} 66% 48%),#253c78)"></b>
  <h3>Task ${i+1}</h3><p>Release notes and browser testing example with laid-out text.</p></article>`).join('');
let scrollFrame;
function beginScroll() {
  let previous = performance.now();
  function tick(now) {
    scrollTo(0, scrollY + Math.min(now - previous, 50) * 1.2);
    previous = now;
    scrollFrame = requestAnimationFrame(tick);
  }
  scrollFrame = requestAnimationFrame(tick);
}
function endScroll() { cancelAnimationFrame(scrollFrame); }
</script>"""
VIDEO = b"""<!doctype html><meta charset=utf-8><title>Browser video fixture</title>
<style>html,body,video { width:100%; height:100%; margin:0; background:#0b0b0c } video { object-fit:cover }</style>
<video id="clip" src="/clip.webm" muted playsinline></video>
<script>const startVideo = clip.play()</script>"""


def serve(clip):
    assets = {"/scroll": ("text/html; charset=utf-8", SCROLL),
              "/video": ("text/html; charset=utf-8", VIDEO),
              "/clip.webm": ("video/webm", clip)}

    class Fixtures(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            if self.path not in assets:
                self.send_error(404)
                return
            media_type, body = assets[self.path]
            self.send_response(200)
            self.send_header("Content-Type", media_type)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *_):
            pass

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Fixtures)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


@contextmanager
def benchmark_url(directory, testufo):
    if testufo:
        yield "https://testufo.com/"
        return
    clip = directory / "clip.webm"
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=60",
                    "-t", "13", "-an", "-c:v", "libvpx", "-deadline", "realtime", "-cpu-used", "6", str(clip)], check=True)
    with serve(clip.read_bytes()) as server:
        try:
            yield f"http://127.0.0.1:{server.server_port}/"
        finally:
            server.shutdown()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--trials", type=int, default=2)
    choices = (*MODES, "bitmap-fallback", "bitmap-no-device", "dmabuf-loss") if LINUX else MODES
    parser.add_argument("--mode", action="append", choices=choices, help="Run a specific rendering path. Can be repeated.")
    parser.add_argument("--skip-build", action="store_true")
    parser.add_argument("--testufo", action="store_true", help="Read the live TestUFO FPS counter alongside the window measurement.")
    args = parser.parse_args()
    if not 1 <= args.trials <= 10:
        parser.error("trials must be between 1 and 10")
    modes = args.mode or MODES
    os.chdir(ROOT)
    output = ROOT / "Athena.UI.HeadlessTests/bin/Debug/net10.0"
    runtime = ROOT / "bin/Debug/net10.0/BrowserRuntime"
    bridge = ROOT / "obj/browser-gpu"
    if not args.skip_build:
        host = "linux" if LINUX else "macos"
        subprocess.run(["bash", f"Scripts/build-browser-gpu-{host}.sh", bridge], check=True)
        subprocess.run(["dotnet", "build", "Athena.UI.HeadlessTests", "-p:UseAppHost=false", "--nologo", "-v", "q"], check=True)
        arch = "arm64" if platform.machine() in ("aarch64", "arm64") else "x64"
        subprocess.run(["python3", "Scripts/stage-browser-runtime.py", ("linux-" if LINUX else "osx-") + arch, runtime.parent], check=True)

    with tempfile.TemporaryDirectory(prefix="athena-preview-benchmark-") as directory:
        path = Path(directory)
        with benchmark_url(path, args.testufo) as url:
            results = []
            prefix = "BROWSER_LIVE_FPS " if args.testufo else "BROWSER_BENCHMARK "
            for trial in range(args.trials):
                for mode in modes if trial % 2 == 0 else reversed(modes):
                    env = dict(os.environ)
                    env.pop("ATHENA_BROWSER_GPU_DIRECTORY", None)
                    env.update({"ATHENA_BROWSER_PREVIEW_BENCHMARK": mode,
                                "ATHENA_BROWSER_BENCHMARK_URL": url,
                                "ATHENA_BROWSER_BENCHMARK_PROFILE": str(path / "browser"),
                                "ATHENA_BROWSER_BENCHMARK_SEED": "0" if results else "1",
                                "ATHENA_BROWSER_RUNTIME_DIRECTORY": str(runtime)})
                    if mode in ("iosurface-metal", "dmabuf-egl", "bitmap-fallback", "bitmap-no-device", "dmabuf-loss"):
                        env["ATHENA_BROWSER_GPU_DIRECTORY"] = str(bridge)
                    print(f"Trial {trial + 1}, {mode}", flush=True)
                    run = subprocess.run(["dotnet", str(output / "Athena.UI.HeadlessTests.dll")], capture_output=True,
                                         text=True, env=env, timeout=90)
                    lines = [line for line in run.stdout.splitlines() if line.startswith(prefix)]
                    if run.returncode != 0 or not lines:
                        raise RuntimeError(run.stdout + run.stderr)
                    print(lines[0], flush=True)
                    results.append(json.loads(lines[0].removeprefix(prefix)))
    if len({(row["Width"], row["Height"]) for row in results}) != 1:
        raise RuntimeError("Display scaling or the preview viewport changed between runs")
    if args.testufo:
        print("\nMode             TestUFO FPS (median)   Window FPS   p95 gap (ms)")
        for mode in dict.fromkeys(modes):
            rows = [row for row in results if row["Mode"] == mode]
            fps = [sample["fps"] for row in rows for sample in row["TestUfo"] if sample["fps"] is not None]
            window = statistics.median(row["Window"]["Fps"] for row in rows)
            gap = statistics.median(row["Window"]["P95Ms"] for row in rows)
            print(f"{mode:<16} {statistics.median(fps):20.1f} {window:12.1f} {gap:14.1f}")
        return
    print("\nMode             Workload   FPS   p95 gap (ms)   Gaps >25 ms (%)   Athena CPU (% 1 core)"
          + ("   Athena + Electron (% 1 core)" if LINUX else ""))
    for mode in dict.fromkeys(modes):
        for workload in ("Scroll", "Video"):
            rows = [row[workload] for row in results if row["Mode"] == mode]
            median = [statistics.median(row[field] for row in rows)
                      for field in ("Fps", "P95Ms", "Over25MsPercent", "AthenaCpuPercent")]
            tree = f" {statistics.median(row['ProcessTreeCpuPercent'] for row in rows):28.1f}" if LINUX else ""
            print(f"{mode:<16} {workload:<8} {median[0]:5.1f} {median[1]:14.1f} {median[2]:17.1f} {median[3]:21.1f}{tree}")


if __name__ == "__main__":
    main()
