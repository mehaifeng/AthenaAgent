#!/usr/bin/env python3
"""Run the real Avalonia browser pane on macOS with a repeatable local workload."""
import argparse
import http.server
import json
import os
from pathlib import Path
import statistics
import subprocess
import tempfile
import threading


ROOT = Path(__file__).resolve().parents[1]
MODES = ("bitmap-opengl", "bitmap-metal", "iosurface-metal")
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--trials", type=int, default=2)
    parser.add_argument("--mode", action="append", choices=MODES, help="Run a specific rendering path. Can be repeated.")
    parser.add_argument("--skip-build", action="store_true")
    args = parser.parse_args()
    if not 1 <= args.trials <= 10:
        parser.error("trials must be between 1 and 10")
    modes = args.mode or MODES
    os.chdir(ROOT)
    output = ROOT / "Athena.UI.HeadlessTests/bin/Debug/net10.0"
    runtime = ROOT / "bin/Debug/net10.0/BrowserRuntime"
    bridge = ROOT / "obj/browser-gpu"
    if not args.skip_build:
        subprocess.run(["bash", "Scripts/build-browser-gpu-macos.sh", bridge], check=True)
        subprocess.run(["dotnet", "build", "Athena.UI.HeadlessTests", "-p:UseAppHost=false", "--nologo", "-v", "q"], check=True)
        subprocess.run(["python3", "Scripts/stage-browser-runtime.py", "osx-" + ("arm64" if os.uname().machine == "arm64" else "x64"), runtime.parent], check=True)

    with tempfile.TemporaryDirectory(prefix="athena-preview-benchmark-") as directory:
        path = Path(directory)
        clip = path / "clip.webm"
        subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=60",
                        "-t", "13", "-an", "-c:v", "libvpx", "-deadline", "realtime", "-cpu-used", "6", str(clip)], check=True)
        with serve(clip.read_bytes()) as server:
            results = []
            for trial in range(args.trials):
                for mode in modes if trial % 2 == 0 else reversed(modes):
                    env = dict(os.environ)
                    env.pop("ATHENA_BROWSER_GPU_DIRECTORY", None)
                    env.update({"ATHENA_BROWSER_PREVIEW_BENCHMARK": mode,
                                "ATHENA_BROWSER_BENCHMARK_URL": f"http://127.0.0.1:{server.server_port}/",
                                "ATHENA_BROWSER_BENCHMARK_PROFILE": str(path / "browser"),
                                "ATHENA_BROWSER_BENCHMARK_SEED": "0" if results else "1",
                                "ATHENA_BROWSER_RUNTIME_DIRECTORY": str(runtime)})
                    if mode == "iosurface-metal":
                        env["ATHENA_BROWSER_GPU_DIRECTORY"] = str(bridge)
                    print(f"Trial {trial + 1}, {mode}", flush=True)
                    run = subprocess.run(["dotnet", str(output / "Athena.UI.HeadlessTests.dll")], capture_output=True,
                                         text=True, env=env, timeout=90)
                    lines = [line for line in run.stdout.splitlines() if line.startswith("BROWSER_BENCHMARK ")]
                    if run.returncode != 0 or not lines:
                        raise RuntimeError(run.stdout + run.stderr)
                    print(lines[0], flush=True)
                    results.append(json.loads(lines[0].removeprefix("BROWSER_BENCHMARK ")))
            server.shutdown()
    if len({(row["Width"], row["Height"]) for row in results}) != 1:
        raise RuntimeError("Display scaling or the preview viewport changed between runs")
    print("\nMode             Workload   FPS   p95 gap (ms)   Gaps >25 ms (%)   Athena CPU (% 1 core)")
    for mode in dict.fromkeys(modes):
        for workload in ("Scroll", "Video"):
            rows = [row[workload] for row in results if row["Mode"] == mode]
            median = [statistics.median(row[field] for row in rows)
                      for field in ("Fps", "P95Ms", "Over25MsPercent", "AthenaCpuPercent")]
            print(f"{mode:<16} {workload:<8} {median[0]:5.1f} {median[1]:14.1f} {median[2]:17.1f} {median[3]:21.1f}")


if __name__ == "__main__":
    main()
