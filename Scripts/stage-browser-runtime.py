#!/usr/bin/env python3
"""Copy the verified browser payload into a publish directory."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import urllib.request

rid, output = sys.argv[1:3]
root = Path(__file__).resolve().parents[1]
manifest = json.loads((root / "Services/ComputerUse/BrowserRuntime.json").read_text())
package = manifest["packages"][rid]
version = manifest["version"]
asset = f"electron-v{version}-{package['asset']}.zip"
cache = Path(os.environ.get("ATHENA_BROWSER_BUILD_CACHE", root / "obj/browser-runtime"))


def valid(path):
    if not path.is_file():
        return False
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest() == package["sha256"]


destination = Path(output) / "BrowserRuntime" / "runtime.zip"
if valid(destination):
    print(f"Bundled {asset} is ready")
    sys.exit(0)

cache.mkdir(parents=True, exist_ok=True)
source = cache / asset
if not valid(source):
    with tempfile.NamedTemporaryFile(dir=cache, delete=False) as target:
        partial = Path(target.name)
    try:
        url = f"https://github.com/electron/electron/releases/download/v{version}/{asset}"
        with urllib.request.urlopen(url, timeout=120) as response, partial.open("wb") as target:
            shutil.copyfileobj(response, target)
        if not valid(partial):
            raise RuntimeError(f"Browser runtime checksum mismatch: {asset}")
        os.replace(partial, source)
    finally:
        partial.unlink(missing_ok=True)

destination.parent.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(dir=destination.parent) as staging:
    staged_archive = Path(staging) / destination.name
    shutil.copyfile(source, staged_archive)
    os.replace(staged_archive, destination)
print(f"Bundled {asset}")
