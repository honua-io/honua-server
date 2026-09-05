#!/usr/bin/env python3
"""Fail-closed decision and marker contract for serving-image evidence reuse."""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from pathlib import Path

SCHEMA = "honua.ci.serving-image-verification/v1"
VARIANTS = ("generic", "lambda", "functions")
DIGEST = re.compile(r"[0-9a-f]{64}")


def _valid_digest(value: str) -> str:
    if DIGEST.fullmatch(value or "") is None:
        raise ValueError("input digest must be 64 lowercase hexadecimal characters")
    return value


def marker(variant: str, digest: str) -> dict[str, str]:
    if variant not in VARIANTS:
        raise ValueError("serving-image variant is invalid")
    return {"schema": SCHEMA, "variant": variant, "input_digest": _valid_digest(digest)}


def decide(enabled: str, variant: str, digest: str, marker_path: Path) -> tuple[bool, str]:
    expected = marker(variant, digest)
    if enabled != "true":
        return False, "HONUA_SERVING_IMAGE_SKIP is not exactly true"
    try:
        value = json.loads(marker_path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return False, "no successful verification marker exists for this content address"
    except (OSError, UnicodeError, json.JSONDecodeError):
        return False, "verification marker is unreadable or malformed"
    if value != expected:
        return False, "verification marker does not match the exact variant and content address"
    return True, "exact content address has successful authoritative verification evidence"


def _append(path_name: str, text: str) -> None:
    destination = os.environ.get(path_name)
    if destination:
        with Path(destination).open("a", encoding="utf-8") as stream:
            stream.write(text)


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    commands = parser.add_subparsers(dest="command", required=True)
    decision = commands.add_parser("decide")
    decision.add_argument("--enabled", required=True)
    decision.add_argument("--variant", required=True, choices=VARIANTS)
    decision.add_argument("--digest", required=True)
    decision.add_argument("--marker", type=Path, required=True)
    prepare = commands.add_parser("prepare-marker")
    prepare.add_argument("--variant", required=True, choices=VARIANTS)
    prepare.add_argument("--digest", required=True)
    prepare.add_argument("--output", type=Path, required=True)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    if args.command == "prepare-marker":
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(
            json.dumps(marker(args.variant, args.digest), sort_keys=True) + "\n",
            encoding="utf-8",
        )
        return 0

    skip, reason = decide(args.enabled, args.variant, args.digest, args.marker)
    decision = "reuse-skip" if skip else "build-and-verify"
    _append("GITHUB_OUTPUT", f"skip={'true' if skip else 'false'}\nreason={reason}\n")
    _append(
        "GITHUB_STEP_SUMMARY",
        "\n".join(
            (
                f"## Serving-image verification: `{args.variant}`",
                "",
                f"- Input digest: `{args.digest}`",
                f"- Decision: `{decision}`",
                f"- Reason: {reason}",
                "",
            )
        ),
    )
    print(f"serving-image-reuse variant={args.variant} digest={args.digest} decision={decision} reason={reason}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except ValueError as error:
        print(f"serving-image-reuse: {error}", file=sys.stderr)
        raise SystemExit(2) from error
