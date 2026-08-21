#!/usr/bin/env python3
"""Keep the public Operate metric inventory aligned with Monitoring metrics."""

from __future__ import annotations

import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
DOC = ROOT / "docs" / "guides" / "operate" / "metrics.md"
INSTRUMENT = re.compile(
    r"\.Create(?:Counter|Histogram|ObservableGauge|UpDownCounter)<[^>]+>\s*\(\s*"
    r'"(honua_[a-z0-9_]+)"',
    re.DOTALL,
)
DOCUMENTED = re.compile(r"`(honua_[a-z0-9_]+)`")


def source_metrics() -> set[str]:
    names: set[str] = set()
    for path in sorted((ROOT / "src").glob("**/Monitoring/*Metrics.cs")):
        names.update(INSTRUMENT.findall(path.read_text(encoding="utf-8")))
    return names


def main() -> int:
    expected = source_metrics()
    actual = set(DOCUMENTED.findall(DOC.read_text(encoding="utf-8")))
    missing = sorted(expected - actual)
    stale = sorted(actual - expected)
    if missing or stale:
        if missing:
            print("Missing Operate metric documentation:", *missing, sep="\n  ", file=sys.stderr)
        if stale:
            print("Documented names not emitted by Monitoring/*Metrics.cs:", *stale, sep="\n  ", file=sys.stderr)
        return 1
    print(f"Operate metric inventory is current ({len(expected)} instruments).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
