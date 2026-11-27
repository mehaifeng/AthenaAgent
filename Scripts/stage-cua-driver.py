#!/usr/bin/env python3
"""Stage the pinned Cua Driver C ABI and its license at build time."""
import hashlib
import os
from pathlib import Path
import shutil
import sys
import tempfile
import urllib.request
import zipfile

PACKAGES = {
    "macosx_13_0_universal2": (
        "https://files.pythonhosted.org/packages/a7/91/3c3c4dbcc5baa0452fc0364aa82e228221909a8bc8d558ed296f84090c06/cua_driver-0.58.1-py3-none-macosx_13_0_universal2.whl",
        "466b86955584a0321fb617dabcf4ba4ed964f6ed90fdbd733e917bd81c1ae23c"),
    "manylinux_2_31_aarch64": (
        "https://files.pythonhosted.org/packages/d3/2d/f679aa5d52855ba13e05ba15796a61ad9244a83bff8823a454444680ee5e/cua_driver-0.58.1-py3-none-manylinux_2_31_aarch64.whl",
        "9e12f9b4db102de3f7f0507cf8581b43bd1a5f0e9a26c105906cebf242650c9e"),
    "manylinux_2_31_x86_64": (
        "https://files.pythonhosted.org/packages/2b/b3/c195e9fc8b588a9d04e8c7f51862977ee5856057adbb1db34be1a3d3333b/cua_driver-0.58.1-py3-none-manylinux_2_31_x86_64.whl",
        "52c3b28a9cf4dce24dd2a05ccd0d51d950bfb8d76703568a47385827367f40fb"),
    "win_amd64": (
        "https://files.pythonhosted.org/packages/c6/f1/67ed684936c352e921181f35707dab1420f19275944743838f3c180d1944/cua_driver-0.58.1-py3-none-win_amd64.whl",
        "f7681225b4e2e92656e1dd773a6d781acf09f5205efcd837acacb9366df407fb"),
    "win_arm64": (
        "https://files.pythonhosted.org/packages/0e/0e/d864ef80ff4300aae9b08ecadc9cfa6fcbea1ae4851f0bd7e030625c1fc6/cua_driver-0.58.1-py3-none-win_arm64.whl",
        "a08d6fdacf1af6bea569285fe0366fb2e39a099f28887698f79c69f6051cf7ab"),
}
PLATFORMS = {
    "linux-x64": ("manylinux_2_31_x86_64", "libcua_driver_sdk.so"),
    "linux-arm64": ("manylinux_2_31_aarch64", "libcua_driver_sdk.so"),
    "osx-x64": ("macosx_13_0_universal2", "libcua_driver_sdk.dylib"),
    "osx-arm64": ("macosx_13_0_universal2", "libcua_driver_sdk.dylib"),
    "win-x64": ("win_amd64", "cua_driver_sdk.dll"),
    "win-arm64": ("win_arm64", "cua_driver_sdk.dll"),
}


def verified(path, digest):
    if not path.is_file():
        return False
    with path.open("rb") as stream:
        checksum = hashlib.sha256()
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            checksum.update(block)
        return checksum.hexdigest() == digest


def stage(rid, target):
    platform, library = PLATFORMS[rid]
    url, digest = PACKAGES[platform]
    wheels = target.parent / "wheels"
    wheels.mkdir(parents=True, exist_ok=True)
    target.mkdir(parents=True, exist_ok=True)
    wheel = wheels / url.rsplit("/", 1)[1]
    with tempfile.TemporaryDirectory(dir=target) as temp:
        staging = Path(temp)
        if not verified(wheel, digest):
            download = staging / "download.whl"
            with urllib.request.urlopen(url, timeout=120) as source, download.open("wb") as output:
                shutil.copyfileobj(source, output)
            if not verified(download, digest):
                raise ValueError(f"Cua Driver checksum mismatch for {rid}")
            os.replace(download, wheel)
        with zipfile.ZipFile(wheel) as package:
            for member, name in [(f"cua_driver/{library}", library),
                                 ("cua_driver-0.58.1.dist-info/licenses/LICENSE", "CuaDriver-LICENSE.txt")]:
                with package.open(member) as source, (staging / name).open("wb") as output:
                    shutil.copyfileobj(source, output)
                os.replace(staging / name, target / name)


if __name__ == "__main__":
    stage(sys.argv[1], Path(sys.argv[2]).resolve())
