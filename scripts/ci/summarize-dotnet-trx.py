#!/usr/bin/env python3
"""Create stable, duration-free parity evidence from one or more TRX files."""

from __future__ import annotations

import argparse
import hashlib
import json
import xml.etree.ElementTree as ET
from collections import Counter
from pathlib import Path

CONTRACT = "honua.dotnet-trx-evidence/v1"


def summarize(paths: list[Path]) -> dict:
    results: list[dict[str, str]] = []
    for path in sorted(paths, key=lambda item: item.as_posix()):
        root = ET.parse(path).getroot()
        for node in root.findall(".//{*}UnitTestResult"):
            name = node.attrib.get("testName")
            outcome = node.attrib.get("outcome")
            if not name or not outcome:
                raise ValueError(f"TRX result in {path} lacks testName/outcome")
            results.append({"name": name, "outcome": outcome})
    if not results:
        raise ValueError("TRX evidence contains no executed tests")
    results.sort(key=lambda item: (item["name"], item["outcome"]))
    canonical = json.dumps(results, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    counts = Counter(item["outcome"] for item in results)
    return {
        "contract": CONTRACT,
        "result_sha256": hashlib.sha256(canonical).hexdigest(),
        "result_count": len(results),
        "outcomes": dict(sorted(counts.items())),
        "results": results,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", type=Path, action="append", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    evidence = summarize(args.input)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(evidence, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(
        f"trx-evidence={evidence['result_count']} "
        f"sha256={evidence['result_sha256']} outcomes={evidence['outcomes']}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
