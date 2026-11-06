#!/usr/bin/env python3
"""Round the corners of the Athena app icon.

The artwork in Assets/AthenaAgent.png is a full-bleed square. Every desktop
that shows it as an app icon expects a rounded silhouette instead:

  * GNOME paints the launcher PNG at whatever size the theme asks for and does
    no masking of its own, so a square reads as a hard tile next to every
    other icon in the grid.
  * macOS does not mask .icns either — a square bundle icon looks broken.
  * The X11/Wayland window icon (Window.Icon -> _NET_WM_ICON) is the same
    artwork, so the dock must get the same silhouette as the app list or the
    two disagree.

This script rebuilds all three shipped assets from one master:

    Assets/AthenaAgent.png   1080px master, corners made transparent
    Assets/Athena.ico        16..256, multi-frame (was a single 128 frame)
    Assets/Athena.icns       the icns entry set iconutil would emit

It is idempotent: re-running it re-applies the same mask and changes nothing.

Usage:
    python3 Scripts/round-app-icon.py [--radius 0.28] [--report]
"""

from __future__ import annotations

import argparse
import struct
import sys
from pathlib import Path

from PIL import Image, ImageDraw

REPO = Path(__file__).resolve().parent.parent
ASSETS = REPO / "Assets"
MASTER = ASSETS / "AthenaAgent.png"
ICO = ASSETS / "Athena.ico"
ICNS = ASSETS / "Athena.icns"

ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]

# (icns type code, edge length in pixels) — the set iconutil emits for an
# .iconset holding 16/32/128/256/512 plus their @2x. All entries are PNG;
# macOS 10.7+ reads icp4..icp6 that way too.
ICNS_ENTRIES = [
    (b"icp4", 16),
    (b"icp5", 32),
    (b"icp6", 64),
    (b"ic07", 128),
    (b"ic08", 256),
    (b"ic09", 512),
    (b"ic10", 1024),
    (b"ic11", 32),
    (b"ic12", 64),
    (b"ic13", 256),
    (b"ic14", 512),
]

SUPERSAMPLE = 4


def rounded_mask(size: int, radius_ratio: float) -> Image.Image:
    """Antialiased rounded-square alpha mask.

    Drawn at SUPERSAMPLE× and downsampled with LANCZOS so the corners stay
    smooth at 16px as well as 1024px.
    """
    big = size * SUPERSAMPLE
    radius = round(big * radius_ratio)
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (0, 0, big - 1, big - 1), radius=radius, fill=255
    )
    return mask.resize((size, size), Image.LANCZOS)


def apply_rounding(img: Image.Image, radius_ratio: float) -> Image.Image:
    out = img.convert("RGBA")
    mask = rounded_mask(out.width, radius_ratio)
    # Preserve any alpha the art already has by intersecting, not replacing.
    if out.getchannel("A").getextrema()[0] < 255:
        existing = out.getchannel("A")
        mask = Image.composite(mask, Image.new("L", mask.size, 0), existing)
    out.putalpha(mask)
    return out


def report_corner_loss(rounded: Image.Image, radius_ratio: float) -> None:
    """How much of the artwork the mask throws away, and how busy it was."""
    rgb = rounded.convert("RGB")
    alpha = rounded.getchannel("A")
    hsv = rgb.convert("HSV")
    sat = hsv.getchannel("S")
    val = hsv.getchannel("V")

    cut_area = sum(1 for a in alpha.getdata() if a < 8)
    total = alpha.width * alpha.height

    busy = 0
    energy = 0
    gray = rgb.convert("L")
    px = gray.load()
    a = alpha.load()
    s = sat.load()
    v = val.load()
    for y in range(alpha.height):
        for x in range(alpha.width):
            if a[x, y] >= 8:
                continue
            if x + 1 < alpha.width:
                energy += abs(px[x, y] - px[x + 1, y])
            if s[x, y] > 70 and v[x, y] > 80:
                busy += 1
    print(f"radius        : {radius_ratio:.3f} of the edge")
    print(f"corner area   : {cut_area / total:.2%} of the canvas")
    print(f"  of which    : {busy / max(cut_area, 1):.2%} is saturated/bright content")
    print(f"  edge energy : {energy / max(cut_area, 1):.1f} per cut pixel")


def write_png(rounded: Image.Image) -> None:
    rounded.save(MASTER, optimize=True)
    print(f"wrote {MASTER.relative_to(REPO)} ({MASTER.stat().st_size:,} bytes)")


def write_ico(master: Image.Image) -> None:
    base = master.resize((256, 256), Image.LANCZOS)
    frames = [base.resize((s, s), Image.LANCZOS) for s in ICO_SIZES if s != 256]
    frames.append(base)
    frames[-1].save(ICO, format="ICO", sizes=[(s, s) for s in ICO_SIZES])
    print(f"wrote {ICO.relative_to(REPO)} frames={ICO_SIZES} ({ICO.stat().st_size:,} bytes)")


def write_icns(master: Image.Image) -> None:
    body = b""
    for code, size in ICNS_ENTRIES:
        frame = master.resize((size, size), Image.LANCZOS)
        buf = Path("/tmp/athena-icns-frame.png")
        frame.save(buf, format="PNG", optimize=True)
        data = buf.read_bytes()
        body += code + struct.pack(">I", len(data) + 8) + data
        buf.unlink()
    payload = b"icns" + struct.pack(">I", len(body) + 8) + body
    ICNS.write_bytes(payload)
    print(f"wrote {ICNS.relative_to(REPO)} entries={len(ICNS_ENTRIES)} ({ICNS.stat().st_size:,} bytes)")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument(
        "--radius",
        type=float,
        default=0.28,
        help="corner radius as a fraction of the edge (default 0.28)",
    )
    ap.add_argument("--report", action="store_true", help="print how much art the mask cuts")
    args = ap.parse_args()

    if not 0.0 < args.radius < 0.5:
        print("error: --radius must be between 0 and 0.5", file=sys.stderr)
        return 2

    master = Image.open(MASTER)
    if master.width != master.height:
        print(f"error: {MASTER.name} is not square ({master.width}x{master.height})", file=sys.stderr)
        return 1

    rounded = apply_rounding(master, args.radius)

    if args.report:
        report_corner_loss(rounded, args.radius)

    write_png(rounded)
    write_ico(rounded)
    write_icns(rounded)

    corners = [rounded.getpixel(p) for p in [(0, 0), (1079, 0), (0, 1079), (1079, 1079)]]
    print(f"corner pixels : {[c[3] for c in corners]} (alpha, 0 = transparent)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
