#!/usr/bin/env python3
"""Compile the CoreUI SVG icons listed in Scripts/coreui-icons.manifest into
Styles/CoreIcons.axaml as Avalonia StreamGeometry resources.

    python3 Scripts/generate-coreui-icons.py [--repo /path/to/coreui-icons]

Without --repo the CoreUI repository is cloned (shallow) into a temp directory.

Two details make the output correct rather than merely plausible:

*   Every geometry is prefixed with ``F1``.  Avalonia's path-markup parser defaults to
    EvenOdd, SVG defaults to nonzero — without the prefix, icons whose subpaths overlap
    (which is every multi-<path> CoreUI icon, since separate <path> elements simply paint
    over each other) would render with holes punched through the overlaps.
*   Multi-<path> icons are concatenated into a single geometry.  That is only equivalent
    to the SVG under nonzero fill, which is what ``F1`` selects.
*   Every geometry carries a zero-coverage frame around the original canvas, so the whole
    set shares one scale instead of each icon being blown up to its own content bounds.

Licensing: the CoreUI Icons Free SVGs are CC BY 4.0 and require attribution, which the
generated header carries (and Docs/ThirdPartyNotices.md restates for shipped builds).  The
brand marks (cib-*) are CC0 but remain trademarks of their owners, so only use one to refer
to the thing it depicts.
"""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MANIFEST = os.path.join(REPO_ROOT, "Scripts", "coreui-icons.manifest")
OUTPUT = os.path.join(REPO_ROOT, "Styles", "CoreIcons.axaml")
UPSTREAM = "https://github.com/coreui/coreui-icons.git"

SVG_NS = "{http://www.w3.org/2000/svg}"

# CoreUI's stroke width as a fraction of the canvas (32 units on 512).
LINE_WEIGHT = 32 / 512

# Per-command parameter counts, and which of those parameters are x/y coordinates.
# Arc is special-cased: rx ry rotation large-arc sweep x y.
COMMANDS = {
    "M": 2, "L": 2, "T": 2,
    "H": 1, "V": 1,
    "C": 6, "S": 4, "Q": 4,
    "A": 7,
    "Z": 0,
}

NUMBER = re.compile(r"[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?")
TOKEN = re.compile(r"([MmLlHhVvCcSsQqTtAaZz])|([-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?)")


def fmt(value: float) -> str:
    """Shortest decimal that stays within half a thousandth of the CoreUI canvas unit."""
    for places in range(7):
        text = f"{value:.{places}f}"
        if abs(float(text) - value) <= 5e-5:
            break
    if "." in text:
        text = text.rstrip("0").rstrip(".")
    return "0" if text in ("", "-0") else text


def tokenize(d: str):
    """Yield (command, [args]) pairs, expanding implicit repeated commands."""
    tokens = [(m.group(1), m.group(2)) for m in TOKEN.finditer(d)]
    index = 0
    command = None
    while index < len(tokens):
        letter, _ = tokens[index]
        if letter:
            command = letter
            index += 1
        elif command is None:
            raise ValueError(f"path data starts with a number: {d[:40]!r}")
        elif command in ("M", "m"):
            # An implicit repeat of moveto is lineto (SVG 8.3.2).
            command = "L" if command == "M" else "l"

        arity = COMMANDS[command.upper()]
        args = []
        for _ in range(arity):
            if index >= len(tokens) or tokens[index][0] is not None:
                raise ValueError(f"truncated {command} command in {d[:40]!r}")
            args.append(float(tokens[index][1]))
            index += 1
        yield command, args
        if arity == 0:
            command = None


def render_path(d: str, scale: float = 1.0, dx: float = 0.0, dy: float = 0.0) -> str:
    """Re-emit SVG path data with a uniform scale and translation applied.

    Implicit repeated commands are made explicit, and the leading moveto is made absolute.
    Both matter when several <path> elements are merged into one geometry: a leading
    relative ``m`` would otherwise resolve against the previous path's endpoint, and simply
    upper-casing it would turn its implicit repeats from linetos-relative into
    linetos-absolute.
    """
    out = []
    for index, (command, args) in enumerate(tokenize(d)):
        if index == 0 and command == "m":
            command = "M"
        upper = command.upper()
        relative = command.islower()
        if upper == "Z":
            out.append(command)
            continue
        if upper == "H":
            args = [args[0] * scale + (0 if relative else dx)]
        elif upper == "V":
            args = [args[0] * scale + (0 if relative else dy)]
        elif upper == "A":
            rx, ry, rot, large, sweep, x, y = args
            args = [
                rx * scale, ry * scale, rot, large, sweep,
                x * scale + (0 if relative else dx),
                y * scale + (0 if relative else dy),
            ]
        else:
            args = [
                value * scale + (0 if relative else (dx if i % 2 == 0 else dy))
                for i, value in enumerate(args)
            ]
        rendered = []
        for i, value in enumerate(args):
            if upper == "A" and i in (3, 4):
                rendered.append(str(int(value)))
            else:
                rendered.append(fmt(value))
        out.append(command + " " + " ".join(rendered))
    return " ".join(out)


def bbox(d: str) -> tuple[float, float, float, float]:
    """Rough bounds from on-path points only; good enough to sanity-check placement."""
    xs, ys = [], []
    x = y = 0.0
    start = (0.0, 0.0)
    for command, args in tokenize(d):
        upper = command.upper()
        relative = command.islower()
        if upper == "Z":
            x, y = start
        elif upper == "H":
            x = x + args[0] if relative else args[0]
        elif upper == "V":
            y = y + args[0] if relative else args[0]
        else:
            nx, ny = args[-2], args[-1]
            x, y = (x + nx, y + ny) if relative else (nx, ny)
            if upper == "M":
                start = (x, y)
        xs.append(x)
        ys.append(y)
    return min(xs), min(ys), max(xs), max(ys)


def pascal(slug: str) -> str:
    return "".join(part[:1].upper() + part[1:] for part in re.split(r"[-_]", slug))


def plus_badge(cx: float, cy: float, span: float, weight: float) -> str:
    """A filled plus of the given span, drawn at the set's own line weight.

    Scaling CoreUI's own ``cil-plus`` down to badge size would scale its 32-unit bars down
    too, leaving a hairline that antialiases to a paler grey than the icon it sits inside —
    it reads as a different colour rather than a smaller glyph.  Redrawing the cross at the
    host icon's weight keeps the two looking like one drawing.
    """
    h, t = span / 2, weight / 2
    points = [
        (cx - t, cy - h), (cx + t, cy - h), (cx + t, cy - t), (cx + h, cy - t),
        (cx + h, cy + t), (cx + t, cy + t), (cx + t, cy + h), (cx - t, cy + h),
        (cx - t, cy + t), (cx - h, cy + t), (cx - h, cy - t), (cx - t, cy - t),
    ]
    head = f"M {fmt(points[0][0])} {fmt(points[0][1])}"
    tail = " ".join(f"L {fmt(x)} {fmt(y)}" for x, y in points[1:])
    return f"{head} {tail} Z"


def canvas_frame(size: float) -> str:
    """A zero-coverage rectangle that pins a geometry's bounds to the full icon canvas.

    PathIcon stretches a geometry by its own *content* bounds, so without this every icon
    would be normalised to a different scale: a full-bleed folder and a 12:1 minus bar would
    both be blown up to fill the control, making the minus a sub-pixel hairline and the
    stroke weights inconsistent across the set.

    The same rectangle is traced clockwise and counter-clockwise.  Under the nonzero rule
    the two windings cancel, so it paints nothing, while the points still count towards the
    path bounds — which is exactly the canvas CoreUI drew the icon on.
    """
    end = fmt(size)
    return f"M 0 0 H {end} V {end} H 0 Z M 0 0 V {end} H {end} V 0 Z"


def read_icon(repo: str, slug: str, brand: bool = False) -> tuple[str, float]:
    folder, prefix = ("brand", "cib-") if brand else ("free", "cil-")
    path = os.path.join(repo, "svg", folder, f"{prefix}{slug}.svg")
    if not os.path.exists(path):
        raise SystemExit(f"missing CoreUI icon: {path}")

    root = ET.parse(path).getroot()
    view_box = root.get("viewBox", "").split()
    # Free icons are drawn on 512x512, brand icons on 32x32; canvas_frame pins each icon to
    # whichever it was drawn on.  A non-square canvas would distort against the rest of the set.
    if len(view_box) != 4 or view_box[:2] != ["0", "0"] or view_box[2] != view_box[3]:
        raise SystemExit(f"{path}: unexpected viewBox {root.get('viewBox')!r}")

    parts = []
    for element in root.iter():
        tag = element.tag.replace(SVG_NS, "")
        if tag == "path":
            if element.get("fill-rule") == "evenodd":
                raise SystemExit(f"{path}: evenodd path cannot be merged into one geometry")
            parts.append(render_path(element.get("d", "").strip()))
        elif tag in ("circle", "rect", "ellipse", "polygon", "polyline", "line"):
            raise SystemExit(f"{path}: <{tag}> is not supported; pick a path-only icon")
    if not parts:
        raise SystemExit(f"{path}: no <path> data")
    return " ".join(parts), float(view_box[2])


def parse_manifest() -> tuple[list[tuple[str, str, bool]], list[tuple[str, str, float, float, float]]]:
    simple, composite = [], []
    with open(MANIFEST, encoding="utf-8") as handle:
        for number, raw in enumerate(handle, 1):
            line = raw.split("#", 1)[0].strip()
            if not line:
                continue
            if "=" in line:
                key, expression = (part.strip() for part in line.split("=", 1))
                match = re.fullmatch(
                    r"([\w-]+)\s*\+\s*([\w-]+)@([\d.]+),([\d.-]+),([\d.-]+)", expression)
                if not match:
                    raise SystemExit(f"{MANIFEST}:{number}: cannot parse composite {expression!r}")
                base, overlay, span, cx, cy = match.groups()
                if overlay != "plus":
                    raise SystemExit(
                        f"{MANIFEST}:{number}: only the 'plus' badge is supported as an overlay")
                composite.append((key, base, float(span), float(cx), float(cy)))
            elif line.startswith("brand:"):
                slug = line[len("brand:"):]
                simple.append((pascal(slug), slug, True))
            else:
                simple.append((pascal(line), line, False))
    return simple, composite


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", help="path to a coreui-icons checkout")
    args = parser.parse_args()

    repo = args.repo
    temp = None
    if not repo:
        temp = tempfile.mkdtemp(prefix="coreui-icons-")
        repo = os.path.join(temp, "coreui-icons")
        print(f"cloning {UPSTREAM} ...", file=sys.stderr)
        subprocess.run(["git", "clone", "--depth", "1", UPSTREAM, repo], check=True)

    simple, composite = parse_manifest()
    entries: list[tuple[str, str]] = []

    drawn: dict[str, tuple[str, float]] = {}
    for key, slug, brand in simple:
        drawn[key] = read_icon(repo, slug, brand)

    for key, base, span, cx, cy in composite:
        base_d, canvas = drawn.get(pascal(base)) or read_icon(repo, base)
        # CoreUI draws every stroke 32 units wide on its 512 canvas; the badge matches it.
        badge = plus_badge(cx, cy, span, canvas * LINE_WEIGHT)
        drawn[key] = (f"{base_d} {badge}", canvas)

    entries = [(key, f"{data} {canvas_frame(canvas)}") for key, (data, canvas) in drawn.items()]

    lines = [
        '<ResourceDictionary xmlns="https://github.com/avaloniaui"',
        '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
        "    <!--",
        "        GENERATED FILE — do not edit by hand.",
        "",
        "        Source: CoreUI Icons Free (https://github.com/coreui/coreui-icons)",
        "        Icons (c) creativeLabs Lukasz Holeczek, licensed CC BY 4.0",
        "        https://creativecommons.org/licenses/by/4.0/",
        "        Brand marks (cib-*) are CC0 but remain trademarks of their respective owners.",
        "        Regenerate: python3 Scripts/generate-coreui-icons.py",
        "        Edit Scripts/coreui-icons.manifest to add or drop an icon.",
        "",
        "        Views must not bind to these keys directly — use the semantic AthenaIcon*",
        "        aliases in Styles/AppIcons.axaml so the vendor stays swappable.",
        "",
        "        The leading F1 selects the nonzero fill rule (Avalonia's path-markup default",
        "        is EvenOdd, SVG's is nonzero); dropping it punches holes through icons whose",
        "        subpaths overlap.",
        "    -->",
    ]
    for key, data in sorted(entries):
        lines.append(f'    <StreamGeometry x:Key="CoreIcon{key}">F1 {data}</StreamGeometry>')
    lines.append("</ResourceDictionary>")
    lines.append("")

    with open(OUTPUT, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(lines))

    print(f"wrote {len(entries)} icons to {os.path.relpath(OUTPUT, REPO_ROOT)}")
    if temp:
        print(f"(clone left in {temp})", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
