#!/usr/bin/env python3
"""Download, verify, compile and stage the pinned CEF browser runtime & C ABI bridge."""
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tarfile
import tempfile
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
MANIFEST_PATH = ROOT / "Services/ComputerUse/BrowserRuntime.json"
BRIDGE_SRC = ROOT / "Native/CefBridge"

MAC_HELPERS = [
    ("Athena Helper", "com.athena.ai.helper"),
    ("Athena Helper (GPU)", "com.athena.ai.helper.gpu"),
    ("Athena Helper (Renderer)", "com.athena.ai.helper.renderer"),
    ("Athena Helper (Plugin)", "com.athena.ai.helper.plugin"),
]


def file_hash(path: Path, algo: str) -> str:
    h = hashlib.new(algo)
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def verify_archive(path: Path, package: dict) -> bool:
    if not path.is_file():
        return False
    if "sha256" in package and file_hash(path, "sha256") != package["sha256"]:
        return False
    if "sha1" in package and file_hash(path, "sha1") != package["sha1"]:
        return False
    return True


def host_rid() -> str:
    sys_name = platform.system().lower()
    arch = platform.machine().lower()
    os_part = "win" if sys_name.startswith("win") else "osx" if sys_name == "darwin" else "linux"
    arch_part = "arm64" if arch in ("arm64", "aarch64") else "x64"
    return f"{os_part}-{arch_part}"


def ensure_cef_sdk(rid: str, version: str, package: dict, cache: Path) -> Path:
    cache.mkdir(parents=True, exist_ok=True)
    archive = cache / package["file"]
    if not verify_archive(archive, package):
        url = "https://cef-builds.spotifycdn.com/" + package["file"].replace("+", "%2B")
        print(f"Downloading {package['file']} ...", flush=True)
        with tempfile.NamedTemporaryFile(dir=cache, delete=False) as tmp:
            partial = Path(tmp.name)
        try:
            with urllib.request.urlopen(url, timeout=300) as resp, partial.open("wb") as out:
                shutil.copyfileobj(resp, out)
            if not verify_archive(partial, package):
                raise RuntimeError(f"CEF archive checksum mismatch: {package['file']}")
            os.replace(partial, archive)
        finally:
            partial.unlink(missing_ok=True)

    sdk_dir = cache / f"{version}-{rid}"
    if not (sdk_dir / "include/capi/cef_app_capi.h").is_file():
        print(f"Extracting {package['file']} ...", flush=True)
        staging = Path(tempfile.mkdtemp(dir=cache, prefix="extract-"))
        try:
            with tarfile.open(archive, "r:bz2") as tf:
                names = tf.getnames()
                top = names[0].split("/")[0]
                tf.extractall(staging, filter="tar")
            extracted = staging / top
            if sdk_dir.exists():
                shutil.rmtree(sdk_dir)
            os.replace(extracted, sdk_dir)
        finally:
            shutil.rmtree(staging, ignore_errors=True)
    return sdk_dir


def bridge_sources_digest(rid: str, version: str) -> str:
    h = hashlib.sha256()
    h.update(f"{rid}:{version}".encode())
    for p in sorted(BRIDGE_SRC.glob("*")):
        if p.is_file():
            h.update(p.name.encode())
            h.update(p.read_bytes())
    return h.hexdigest()


def write_helper_plist(bundle_dir: Path, name: str, bundle_id: str, version: str) -> None:
    plist = f"""<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
\t<key>CFBundleDevelopmentRegion</key>
\t<string>en</string>
\t<key>CFBundleDisplayName</key>
\t<string>{name}</string>
\t<key>CFBundleExecutable</key>
\t<string>{name}</string>
\t<key>CFBundleIdentifier</key>
\t<string>{bundle_id}</string>
\t<key>CFBundleInfoDictionaryVersion</key>
\t<string>6.0</string>
\t<key>CFBundleName</key>
\t<string>{name}</string>
\t<key>CFBundlePackageType</key>
\t<string>APPL</string>
\t<key>CFBundleShortVersionString</key>
\t<string>{version}</string>
\t<key>CFBundleVersion</key>
\t<string>{version}</string>
\t<key>LSBackgroundOnly</key>
\t<true/>
\t<key>LSUIElement</key>
\t<true/>
\t<key>NSSupportsAutomaticGraphicsSwitching</key>
\t<true/>
</dict>
</plist>
"""
    (bundle_dir / "Contents/Info.plist").write_text(plist, encoding="utf-8")


def build_mac(sdk_dir: Path, build_dir: Path, rid: str, api_version: int, version: str) -> None:
    arch = "arm64" if rid.endswith("arm64") else "x86_64"
    bridge_out = build_dir / "libathena_cef_bridge.dylib"
    helper_out = build_dir / "athena_cef_helper"
    dylib_stub = sdk_dir / "libcef_dll/wrapper/libcef_dll_dylib.cc"
    loader_mm = sdk_dir / "libcef_dll/wrapper/cef_scoped_library_loader_mac.mm"
    common = [
        "xcrun", "clang++", "-std=c++20", "-O2", "-arch", arch,
        f"-DCEF_API_VERSION={api_version}",
        "-DATHENA_CEF_BRIDGE_BUILD",
        "-DWRAPPING_CEF_SHARED",
        "-I", str(sdk_dir),
        "-I", str(BRIDGE_SRC),
    ]
    subprocess.run(
        common + [
            "-shared", "-fobjc-arc", "-fvisibility=hidden",
            "-Wall", "-Wextra", "-Wno-unused-parameter", "-Wno-missing-field-initializers",
            str(BRIDGE_SRC / "cef_bridge_core.cpp"),
            str(BRIDGE_SRC / "cef_bridge_mac.mm"),
            str(dylib_stub),
            str(loader_mm),
            "-framework", "Cocoa",
            "-framework", "AppKit",
            "-framework", "CoreFoundation",
            "-install_name", "@rpath/libathena_cef_bridge.dylib",
            "-o", str(bridge_out),
        ],
        check=True,
    )
    subprocess.run(
        common + [
            "-Wall", "-Wextra", "-Wno-unused-parameter", "-Wno-missing-field-initializers",
            str(BRIDGE_SRC / "cef_helper_main.cpp"),
            str(dylib_stub),
            str(loader_mm),
            "-framework", "Cocoa",
            "-framework", "CoreFoundation",
            "-o", str(helper_out),
        ],
        check=True,
    )
    subprocess.run(["codesign", "--force", "--sign", "-", str(bridge_out)], check=True)
    subprocess.run(["codesign", "--force", "--sign", "-", str(helper_out)], check=True)


def build_linux(sdk_dir: Path, build_dir: Path, api_version: int) -> None:
    cxx = shutil.which("clang++") or shutil.which("g++")
    if not cxx:
        raise RuntimeError("A C++ compiler (clang++ or g++) is required to build the Linux CEF bridge.")
    bridge_out = build_dir / "libathena_cef_bridge.so"
    helper_out = build_dir / "athena_cef_helper"
    release_dir = sdk_dir / "Release"
    common = [
        cxx, "-std=c++20", "-O2",
        f"-DCEF_API_VERSION={api_version}",
        "-DATHENA_CEF_BRIDGE_BUILD",
        "-I", str(sdk_dir),
        "-I", str(BRIDGE_SRC),
        f"-L{release_dir}",
        "-Wl,-rpath,$ORIGIN",
    ]
    subprocess.run(
        common + [
            "-shared", "-fPIC", "-fvisibility=hidden",
            "-Wall", "-Wextra", "-Wno-unused-parameter",
            str(BRIDGE_SRC / "cef_bridge_core.cpp"),
            str(BRIDGE_SRC / "cef_bridge_linux.cpp"),
            "-lcef", "-lX11", "-lpthread", "-ldl",
            "-o", str(bridge_out),
        ],
        check=True,
    )
    subprocess.run(
        common + [
            "-Wall", "-Wextra", "-Wno-unused-parameter",
            str(BRIDGE_SRC / "cef_helper_main.cpp"),
            "-lcef", "-lpthread", "-ldl",
            "-o", str(helper_out),
        ],
        check=True,
    )


def find_windows_vcvars(rid: str) -> tuple[str, str]:
    arch = "arm64" if rid.endswith("arm64") else "x64"
    vswhere = Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / \
        "Microsoft Visual Studio/Installer/vswhere.exe"
    if vswhere.is_file():
        out = subprocess.check_output(
            [str(vswhere), "-latest", "-products", "*", "-requires",
             "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"],
            text=True,
        ).strip()
        if out:
            vcvars = Path(out) / "VC/Auxiliary/Build/vcvarsall.bat"
            if vcvars.is_file():
                return str(vcvars), arch
    raise RuntimeError("Visual Studio C++ build tools (cl.exe) are required to build the Windows CEF bridge.")


def build_windows(sdk_dir: Path, build_dir: Path, rid: str, api_version: int) -> None:
    bridge_out = build_dir / "athena_cef_bridge.dll"
    helper_out = build_dir / "athena_cef_helper.exe"
    libcef = sdk_dir / "Release/libcef.lib"
    flags = f'/std:c++20 /O2 /MD /EHsc /DCEF_API_VERSION={api_version} /DATHENA_CEF_BRIDGE_BUILD /DUNICODE /D_UNICODE /I"{sdk_dir}" /I"{BRIDGE_SRC}"'
    cmd_bridge = (
        f'cl /nologo {flags} /LD "{BRIDGE_SRC / "cef_bridge_core.cpp"}" "{BRIDGE_SRC / "cef_bridge_win.cpp"}" '
        f'/Fe:"{bridge_out}" /link "{libcef}" user32.lib'
    )
    cmd_helper = (
        f'cl /nologo {flags} "{BRIDGE_SRC / "cef_helper_main.cpp"}" '
        f'/Fe:"{helper_out}" /link "{libcef}" /SUBSYSTEM:WINDOWS user32.lib'
    )
    if shutil.which("cl"):
        subprocess.run(cmd_bridge, shell=True, check=True, cwd=str(build_dir))
        subprocess.run(cmd_helper, shell=True, check=True, cwd=str(build_dir))
    else:
        vcvars, arch = find_windows_vcvars(rid)
        script = f'call "{vcvars}" {arch} && {cmd_bridge} && {cmd_helper}'
        subprocess.run(["cmd", "/c", script], check=True, cwd=str(build_dir))


def stage_mac(sdk_dir: Path, build_dir: Path, dest: Path, version: str) -> None:
    frameworks = dest / "Frameworks"
    frameworks.mkdir(parents=True, exist_ok=True)
    fw_src = sdk_dir / "Release/Chromium Embedded Framework.framework"
    fw_dst = frameworks / "Chromium Embedded Framework.framework"
    if fw_dst.exists():
        shutil.rmtree(fw_dst)
    shutil.copytree(fw_src, fw_dst, symlinks=True)

    shutil.copy2(build_dir / "libathena_cef_bridge.dylib", dest / "libathena_cef_bridge.dylib")

    for name, bundle_id in MAC_HELPERS:
        app_dir = frameworks / f"{name}.app"
        if app_dir.exists():
            shutil.rmtree(app_dir)
        macos_dir = app_dir / "Contents/MacOS"
        macos_dir.mkdir(parents=True, exist_ok=True)
        exe_dst = macos_dir / name
        shutil.copy2(build_dir / "athena_cef_helper", exe_dst)
        exe_dst.chmod(0o755)
        write_helper_plist(app_dir, name, bundle_id, version)
        subprocess.run(["codesign", "--force", "--sign", "-", str(app_dir)], check=True)

    # Pseudo-bundle Info.plist so loose `dotnet run` passes CEF's main_bundle_path and
    # CFBundleIdentifier checks cleanly (prevents empty bundle ID in MachPortRendezvous).
    pseudo_contents = dest / "Contents"
    pseudo_contents.mkdir(parents=True, exist_ok=True)
    pseudo_plist = f"""<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
\t<key>CFBundleDevelopmentRegion</key>
\t<string>en</string>
\t<key>CFBundleDisplayName</key>
\t<string>Athena</string>
\t<key>CFBundleExecutable</key>
\t<string>Athena.UI</string>
\t<key>CFBundleIdentifier</key>
\t<string>com.athena.ai</string>
\t<key>CFBundleInfoDictionaryVersion</key>
\t<string>6.0</string>
\t<key>CFBundleName</key>
\t<string>Athena</string>
\t<key>CFBundlePackageType</key>
\t<string>APPL</string>
\t<key>CFBundleShortVersionString</key>
\t<string>{version}</string>
\t<key>CFBundleVersion</key>
\t<string>{version}</string>
</dict>
</plist>
"""
    (pseudo_contents / "Info.plist").write_text(pseudo_plist, encoding="utf-8")
    fw_link = pseudo_contents / "Frameworks"
    if fw_link.is_symlink() or fw_link.exists():
        fw_link.unlink() if fw_link.is_symlink() else shutil.rmtree(fw_link)
    os.symlink("../Frameworks", fw_link)

    subprocess.run(["codesign", "--force", "--sign", "-", str(dest / "libathena_cef_bridge.dylib")], check=True)


def stage_flat(sdk_dir: Path, build_dir: Path, dest: Path, rid: str) -> None:
    release_dir = sdk_dir / "Release"
    resources_dir = sdk_dir / "Resources"
    for src_dir in (release_dir, resources_dir):
        if not src_dir.is_dir():
            continue
        for item in src_dir.iterdir():
            if item.suffix in (".lib", ".pdb", ".exp"):
                continue
            target = dest / item.name
            if item.is_dir():
                if target.exists():
                    shutil.rmtree(target)
                shutil.copytree(item, target, symlinks=True)
            else:
                shutil.copy2(item, target)
    if rid.startswith("win-"):
        shutil.copy2(build_dir / "athena_cef_bridge.dll", dest / "athena_cef_bridge.dll")
        shutil.copy2(build_dir / "athena_cef_helper.exe", dest / "athena_cef_helper.exe")
    else:
        shutil.copy2(build_dir / "libathena_cef_bridge.so", dest / "libathena_cef_bridge.so")
        helper = dest / "athena_cef_helper"
        shutil.copy2(build_dir / "athena_cef_helper", helper)
        helper.chmod(0o755)


def main() -> None:
    if len(sys.argv) < 3:
        raise SystemExit("Usage: stage-browser-runtime.py <rid> <output_dir>")
    rid = sys.argv[1]
    output = Path(sys.argv[2]).resolve()
    manifest = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))
    if rid not in manifest["packages"]:
        raise RuntimeError(f"Unsupported browser runtime RID: {rid}")
    package = manifest["packages"][rid]
    version = manifest["version"]
    api_version = int(manifest["apiVersion"])

    current_host = host_rid()
    if rid.split("-")[0] != current_host.split("-")[0]:
        raise RuntimeError(
            f"Native CEF bridge for {rid} must be built on a matching OS host (current host: {current_host})."
        )

    cache = Path(os.environ.get("ATHENA_BROWSER_BUILD_CACHE", ROOT / "obj/cef-runtime"))
    sdk_dir = ensure_cef_sdk(rid, version, package, cache)

    digest = bridge_sources_digest(rid, version)
    dest = output / "BrowserRuntime"
    stamp = dest / ".cef-stamp"
    if stamp.is_file() and stamp.read_text(encoding="utf-8").strip() == digest:
        print(f"Bundled CEF {version} ({rid}) is up to date")
        return

    build_dir = cache / f"bridge-{rid}-{digest[:12]}"
    bridge_done = build_dir / ".built"
    if not bridge_done.is_file():
        build_dir.mkdir(parents=True, exist_ok=True)
        print(f"Compiling native CEF C ABI bridge for {rid} ...", flush=True)
        if rid.startswith("osx-"):
            build_mac(sdk_dir, build_dir, rid, api_version, version)
        elif rid.startswith("win-"):
            build_windows(sdk_dir, build_dir, rid, api_version)
        else:
            build_linux(sdk_dir, build_dir, api_version)
        bridge_done.write_text(digest, encoding="utf-8")

    staging = Path(tempfile.mkdtemp(dir=output, prefix=".cef-stage-"))
    try:
        if rid.startswith("osx-"):
            stage_mac(sdk_dir, build_dir, staging, version)
        else:
            stage_flat(sdk_dir, build_dir, staging, rid)
        shutil.copy2(sdk_dir / "LICENSE.txt", staging / "CEF-LICENSE.txt")
        (staging / ".cef-stamp").write_text(digest, encoding="utf-8")
        if dest.exists():
            shutil.rmtree(dest)
        os.replace(staging, dest)
    finally:
        shutil.rmtree(staging, ignore_errors=True)

    print(f"Bundled CEF {version} ({rid}) -> {dest}")


if __name__ == "__main__":
    main()
