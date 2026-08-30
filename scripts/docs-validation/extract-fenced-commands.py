#!/usr/bin/env python3
"""Extract annotated shell fences from executable documentation."""

from __future__ import annotations

import argparse
import re
import shlex
import sys
from pathlib import Path


ANNOTATION = re.compile(r"^<!-- docs-validation:(?P<id>[a-z0-9.-]+) (?P<attrs>.+) -->$")
FENCE = re.compile(r"^```(?P<language>bash|sh)\s*$")


def fail(message: str) -> None:
    raise ValueError(message)


def parse(document: Path) -> list[tuple[str, str, str]]:
    lines = document.read_text(encoding="utf-8").splitlines()
    blocks: list[tuple[str, str, str]] = []
    ids: set[str] = set()
    pending: tuple[str, dict[str, str], int] | None = None
    index = 0

    while index < len(lines):
        line = lines[index]
        annotation = ANNOTATION.match(line)
        if annotation:
            if pending is not None:
                fail(f"line {index + 1}: annotation does not immediately precede a shell fence")
            try:
                attrs = dict(token.split("=", 1) for token in shlex.split(annotation.group("attrs")))
            except ValueError as error:
                fail(f"line {index + 1}: invalid annotation attributes: {error}")
            block_id = annotation.group("id")
            if block_id in ids:
                fail(f"line {index + 1}: duplicate docs-validation id {block_id}")
            ids.add(block_id)
            pending = (block_id, attrs, index + 1)
            index += 1
            continue

        fence = FENCE.match(line)
        if fence:
            if pending is None:
                fail(f"line {index + 1}: shell fence has no docs-validation annotation")
            block_id, attrs, annotation_line = pending
            pending = None
            mode = attrs.get("mode")
            if mode not in {"run", "skip"}:
                fail(f"line {annotation_line}: mode must be run or skip")
            if mode == "skip" and not attrs.get("reason"):
                fail(f"line {annotation_line}: skipped block requires reason")
            content: list[str] = []
            index += 1
            while index < len(lines) and lines[index] != "```":
                content.append(lines[index])
                index += 1
            if index == len(lines):
                fail(f"line {annotation_line}: unterminated shell fence")
            blocks.append((block_id, mode, "\n".join(content)))
        elif pending is not None and line.strip():
            fail(f"line {pending[2]}: annotation does not immediately precede a shell fence")
        index += 1

    if pending is not None:
        fail(f"line {pending[2]}: annotation has no shell fence")
    if not blocks:
        fail("no annotated shell fences found")
    return blocks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("document", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--list", action="store_true")
    args = parser.parse_args()

    try:
        blocks = parse(args.document)
    except (OSError, ValueError) as error:
        print(f"docs-validation: {error}", file=sys.stderr)
        return 1

    if args.list:
        for block_id, mode, _ in blocks:
            print(f"{mode}\t{block_id}")

    if args.output:
        runnable = [(block_id, content) for block_id, mode, content in blocks if mode == "run"]
        script = ["#!/usr/bin/env bash", "set -euo pipefail", ""]
        for block_id, content in runnable:
            script.extend((f"printf '\\n>>> docs-validation: {block_id}\\n'", content, ""))
        args.output.write_text("\n".join(script), encoding="utf-8")
        args.output.chmod(0o755)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
