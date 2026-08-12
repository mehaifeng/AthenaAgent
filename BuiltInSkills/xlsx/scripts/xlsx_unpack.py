#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Safely unpack an OOXML workbook for expert XML editing.

Raw bytes are preserved by default. Use --pretty only for human inspection;
pretty-printing rewrites every XML part and is unsuitable for fidelity-sensitive edits.
"""

import argparse
import os
import shutil
import sys
import xml.dom.minidom
import zipfile

import runtime_compat  # noqa: F401 - configure UTF-8 console output on Windows

MAX_ENTRIES = 20_000
MAX_ENTRY_BYTES = 128 * 1024 * 1024
MAX_TOTAL_BYTES = 512 * 1024 * 1024
MAX_RATIO = 200


def pretty_print_xml(content: bytes) -> bytes:
    dom = xml.dom.minidom.parseString(content)
    rendered = dom.toprettyxml(indent="  ", encoding="utf-8").decode("utf-8")
    return ("\n".join(line for line in rendered.splitlines() if line.strip()) + "\n").encode("utf-8")


def _validate_members(archive: zipfile.ZipFile, output_dir: str) -> None:
    members = archive.infolist()
    if len(members) > MAX_ENTRIES:
        raise ValueError(f"package contains more than {MAX_ENTRIES} entries")
    root = os.path.realpath(output_dir)
    total = 0
    for member in members:
        target = os.path.realpath(os.path.join(root, member.filename))
        if target != root and not target.startswith(root + os.sep):
            raise ValueError(f"entry escapes output directory: {member.filename}")
        if member.file_size > MAX_ENTRY_BYTES:
            raise ValueError(f"entry exceeds {MAX_ENTRY_BYTES} bytes: {member.filename}")
        total += member.file_size
        if total > MAX_TOTAL_BYTES:
            raise ValueError(f"uncompressed package exceeds {MAX_TOTAL_BYTES} bytes")
        if member.compress_size and member.file_size / member.compress_size > MAX_RATIO:
            raise ValueError(f"suspicious compression ratio: {member.filename}")


def unpack(xlsx_path: str, output_dir: str, *, force: bool = False, pretty: bool = False) -> None:
    if not os.path.isfile(xlsx_path):
        raise FileNotFoundError(xlsx_path)
    if os.path.exists(output_dir):
        if not force:
            raise FileExistsError(f"output already exists; choose a new directory or pass --force: {output_dir}")
        resolved = os.path.realpath(output_dir)
        if resolved in (os.path.realpath(os.sep), os.path.realpath(os.path.expanduser("~"))):
            raise ValueError("refusing to replace a filesystem root or home directory")
        shutil.rmtree(resolved)
    os.makedirs(output_dir)

    try:
        with zipfile.ZipFile(xlsx_path, "r") as archive:
            _validate_members(archive, output_dir)
            archive.extractall(output_dir)
    except Exception:
        shutil.rmtree(output_dir, ignore_errors=True)
        raise

    rewritten = 0
    if pretty:
        for dirpath, _, filenames in os.walk(output_dir):
            for filename in filenames:
                if not filename.lower().endswith((".xml", ".rels")):
                    continue
                path = os.path.join(dirpath, filename)
                with open(path, "rb") as stream:
                    content = stream.read()
                try:
                    content = pretty_print_xml(content)
                except Exception as exc:
                    raise ValueError(f"cannot pretty-print {path}: {exc}") from exc
                with open(path, "wb") as stream:
                    stream.write(content)
                rewritten += 1

    mode = f"pretty-printed {rewritten} XML parts" if pretty else "preserved raw OOXML part bytes"
    print(f"Unpacked '{xlsx_path}' -> '{output_dir}' ({mode})")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", help="source .xlsx or .xlsm")
    parser.add_argument("output_dir", help="new working directory")
    parser.add_argument("--force", action="store_true", help="replace an existing, narrowly resolved output directory")
    parser.add_argument("--pretty", action="store_true", help="rewrite XML for readability (lossy; inspection only)")
    args = parser.parse_args()
    try:
        unpack(args.input, args.output_dir, force=args.force, pretty=args.pretty)
        return 0
    except (OSError, ValueError, zipfile.BadZipFile) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
