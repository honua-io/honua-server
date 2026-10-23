#!/usr/bin/env python3
"""Require every staged observation to identify the source that built the server."""
import argparse
import json
from pathlib import Path
import re


def validate(root: Path, expected: str) -> int:
    if not re.fullmatch(r"[0-9a-f]{40}", expected):
        raise ValueError("expected server commit must be a full lowercase Git SHA")
    envelopes = sorted(root.rglob("*.cert.json"))
    if not envelopes:
        raise ValueError("no certification envelopes to bind to the server source")
    for path in envelopes:
        envelope = json.loads(path.read_text())
        # Analyst/QGIS receipts separate version from commit; the older
        # browser/GDAL/stub schema calls its source identity server_version.
        source = envelope.get("server_commit", envelope.get("server_version"))
        if source != expected:
            raise ValueError(f"{path}: server source {source!r} does not match {expected}")
    return len(envelopes)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    parser.add_argument("expected")
    args = parser.parse_args()
    print(f"Validated {validate(args.root, args.expected)} source-bound certification envelopes")
