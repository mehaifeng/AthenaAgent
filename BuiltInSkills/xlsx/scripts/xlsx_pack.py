#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Validate and atomically pack an OOXML working directory."""

import argparse
import os
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile

import runtime_compat  # noqa: F401 - configure UTF-8 console output on Windows


def validate_xml_files(source_dir: str) -> list[str]:
    bad: list[str] = []
    for dirpath, _, filenames in os.walk(source_dir):
        for filename in filenames:
            if not filename.lower().endswith((".xml", ".rels")):
                continue
            path = os.path.join(dirpath, filename)
            try:
                ET.parse(path)
            except (ET.ParseError, OSError) as exc:
                bad.append(f"{os.path.relpath(path, source_dir)}: {exc}")
    return bad


def pack(source_dir: str, output_path: str, *, overwrite: bool = False) -> None:
    source_dir = os.path.realpath(source_dir)
    output_path = os.path.abspath(output_path)
    if not os.path.isdir(source_dir):
        raise FileNotFoundError(source_dir)
    if not os.path.isfile(os.path.join(source_dir, "[Content_Types].xml")):
        raise ValueError("missing required [Content_Types].xml at package root")
    if os.path.exists(output_path) and not overwrite:
        raise FileExistsError(f"output exists; choose a new path or pass --overwrite: {output_path}")
    failures = validate_xml_files(source_dir)
    if failures:
        raise ValueError("malformed OOXML parts:\n  " + "\n  ".join(failures))

    output_dir = os.path.dirname(output_path) or os.getcwd()
    os.makedirs(output_dir, exist_ok=True)
    handle, temporary_path = tempfile.mkstemp(prefix=".xlsx_pack_", suffix=".tmp", dir=output_dir)
    os.close(handle)
    try:
        count = 0
        with zipfile.ZipFile(temporary_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for dirpath, dirnames, filenames in os.walk(source_dir):
                dirnames.sort()
                for filename in sorted(filenames):
                    path = os.path.join(dirpath, filename)
                    arcname = os.path.relpath(path, source_dir).replace(os.sep, "/")
                    archive.write(path, arcname)
                    count += 1
        os.replace(temporary_path, output_path)
    finally:
        if os.path.exists(temporary_path):
            os.remove(temporary_path)
    print(f"Packed {count} files -> '{output_path}' ({os.path.getsize(output_path):,} bytes)")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source_dir")
    parser.add_argument("output")
    parser.add_argument("--overwrite", action="store_true")
    args = parser.parse_args()
    try:
        pack(args.source_dir, args.output, overwrite=args.overwrite)
        return 0
    except (OSError, ValueError, zipfile.BadZipFile) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
