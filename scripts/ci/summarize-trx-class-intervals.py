#!/usr/bin/env python3
"""Summarise per-class wall-clock intervals from a directory of shard TRX files.

`audit-shard-headroom.py` answers "does this shard fit its cap". This answers
the follow-up question a capacity split needs: *which classes* are the cap, and
would splitting them actually help. It reads the `startTime`/`endTime` on every
`UnitTestResult` — not the `duration`, which excludes fixture/collection setup
and therefore under-reports an integration shard by more than half.

Two measures are reported per class or group, and the gap between them is the
signal:

* **span** — first start to last end. Additive across classes only when the
  shard runs serially.
* **union** — the merged busy intervals. Use this when the shard runs
  collections in parallel, where spans overlap and summing them overcounts.

If the summed per-class spans reproduce the whole run's span, the shard is
serial and class placement is directly additive, so moving a class moves its
whole interval. If they exceed it, use the union columns instead.

Usage (see docs/internal/ci/shard-timeout-budgets.md, "Re-basing the budgets")::

    gh run download <run-id> -n server-test-results-<suffix> -D ./artifacts/<run-id>
    scripts/ci/summarize-trx-class-intervals.py --trx-dir ./artifacts
    scripts/ci/summarize-trx-class-intervals.py --trx-dir ./artifacts --group-depth 4
"""

from __future__ import annotations

import argparse
import collections
import datetime as dt
import math
import statistics
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def percentile(values: list[float], fraction: float) -> float:
    """Nearest-rank percentile, matching audit-shard-headroom.py."""
    ordered = sorted(values)
    rank = max(1, math.ceil(fraction * len(ordered)))
    return ordered[min(len(ordered), rank) - 1]


def union_seconds(intervals: list[tuple[dt.datetime, dt.datetime]]) -> float:
    """Total wall time during which at least one of `intervals` was running."""
    ordered = sorted(intervals)
    total = 0.0
    cur_start, cur_end = ordered[0]
    for start, end in ordered[1:]:
        if start > cur_end:
            total += (cur_end - cur_start).total_seconds()
            cur_start, cur_end = start, end
        else:
            cur_end = max(cur_end, end)
    return total + (cur_end - cur_start).total_seconds()


def read_run(path: Path, group_depth: int) -> tuple[dict[str, list], list]:
    """Return ({group: [(start, end), ...]}, [every interval in the run])."""
    root = ET.parse(path).getroot()
    class_of: dict[str, str] = {}
    for unit_test in root.iter(NS + "UnitTest"):
        method = unit_test.find(NS + "TestMethod")
        if method is not None:
            class_of[unit_test.get("id")] = method.get("className")
    grouped: dict[str, list] = collections.defaultdict(list)
    every: list = []
    for result in root.iter(NS + "UnitTestResult"):
        started = result.get("startTime")
        if not started:
            continue
        interval = (dt.datetime.fromisoformat(started),
                    dt.datetime.fromisoformat(result.get("endTime")))
        fqn = class_of.get(result.get("testId"), "<unknown>")
        key = fqn if group_depth <= 0 else ".".join(fqn.split(".")[:group_depth])
        grouped[key].append(interval)
        every.append(interval)
    return grouped, every


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--trx-dir", required=True, type=Path,
                        help="directory searched recursively for *.trx")
    parser.add_argument("--group-depth", type=int, default=0,
                        help="group by the first N namespace segments (0 = per class)")
    args = parser.parse_args()

    files = sorted(args.trx_dir.rglob("*.trx"))
    if not files:
        print(f"::error::no *.trx under {args.trx_dir}", file=sys.stderr)
        return 1

    spans: dict[str, list[float]] = collections.defaultdict(list)
    unions: dict[str, list[float]] = collections.defaultdict(list)
    whole_span: list[float] = []
    summed_span: list[float] = []
    for path in files:
        grouped, every = read_run(path, args.group_depth)
        if not every:
            continue
        whole_span.append((max(e for _, e in every) - min(s for s, _ in every)).total_seconds())
        run_total = 0.0
        for key, intervals in grouped.items():
            ordered = sorted(intervals)
            span = (ordered[-1][1] - ordered[0][0]).total_seconds()
            spans[key].append(span)
            unions[key].append(union_seconds(intervals))
            run_total += span
        summed_span.append(run_total)

    print(f"runs: {len(whole_span)}   whole-run span p50 {statistics.median(whole_span)/60:.1f} min"
          f"   summed per-class spans p50 {statistics.median(summed_span)/60:.1f} min")
    ratio = statistics.median(summed_span) / statistics.median(whole_span)
    print(f"serial? summed/whole = {ratio:.2f} "
          f"({'serial - spans are additive' if ratio < 1.1 else 'parallel - use the union columns'})\n")
    print(f"{'span p50':>9} {'span p90':>9} {'union p50':>10} {'union p90':>10} {'runs':>5}  key")
    for key in sorted(spans, key=lambda k: -statistics.median(spans[k])):
        print(f"{statistics.median(spans[key])/60:9.2f} {percentile(spans[key], 0.9)/60:9.2f}"
              f" {statistics.median(unions[key])/60:10.2f} {percentile(unions[key], 0.9)/60:10.2f}"
              f" {len(spans[key]):5d}  {key}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
