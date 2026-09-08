#!/usr/bin/env python3
"""Read complete, bounded Actions run catalogs without the 1,000-result cutoff."""

from __future__ import annotations

import argparse
import json
import subprocess
import time
from datetime import datetime, timezone
from pathlib import Path


def timestamp(value: str) -> int:
    return int(datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp())


def iso(value: int) -> str:
    return datetime.fromtimestamp(value, timezone.utc).isoformat().replace("+00:00", "Z")


def github_page(endpoint: str, parameters: dict) -> dict:
    command = ["gh", "api", "--method", "GET", endpoint]
    for key, value in parameters.items():
        command.extend(["-f", f"{key}={value}"])
    for delay in (10, 30, 60, 120, None):
        result = subprocess.run(command, capture_output=True, text=True, timeout=90)
        if result.returncode == 0:
            return json.loads(result.stdout)
        # Never re-authenticate on 403. Authorization failures remain errors.
        transient = any(message in result.stderr.lower() for message in (
            "error connecting", "could not resolve host", "connection reset",
            "timeout", "timed out", "http 502", "http 503", "http 504",
        ))
        if not transient or "403" in result.stderr or delay is None:
            raise RuntimeError(result.stderr.strip())
        time.sleep(delay)
    raise AssertionError("unreachable")


def collect(endpoint: str, lower: int, upper: int, maximum: int, *,
            event: str | None = None, fetch=github_page, pause=time.sleep) -> list[list[dict]]:
    """Split inclusive creation ranges; retry whole slices when paging races.

    Do not filter by status: completing a run must not change page membership.
    Reruns/deletions can still race a read, so totals and identities are checked
    before a slice is accepted. All pages remain in memory until complete.
    """
    if not 1 <= maximum <= 1000 or lower > upper:
        raise ValueError("invalid catalog bound")
    queries = 0

    def sliced(start: int, end: int) -> list[list[dict]]:
        nonlocal queries
        queries += 1
        if queries > 255:
            raise ValueError("workflow catalog exceeds 255 time slices")
        parameters = {"per_page": 100, "created": f"{iso(start)}..{iso(end)}"}
        if event:
            parameters["event"] = event
        for attempt in range(3):
            pages = []
            identifiers = set()
            expected = None
            for number in range(1, 11):
                page = fetch(endpoint, {**parameters, "page": number})
                total = page.get("total_count")
                rows = page.get("workflow_runs")
                if type(total) is not int or total < 0 or not isinstance(rows, list):
                    raise ValueError("invalid workflow catalog page")
                if expected is None:
                    expected = total
                    if total > maximum:
                        if start == end:
                            raise ValueError("one second exceeds the workflow query ceiling")
                        middle = (start + end) // 2
                        return sliced(start, middle) + sliced(middle + 1, end)
                if total != expected or len(rows) != min(100, expected - len(identifiers)):
                    break
                for row in rows:
                    if not isinstance(row, dict) or type(row.get("id")) is not int:
                        raise ValueError("invalid workflow catalog identity")
                    if not start <= timestamp(row["created_at"]) <= end:
                        raise ValueError("workflow outside requested time slice")
                ids = [row["id"] for row in rows]
                if len(set(ids)) != len(ids) or identifiers.intersection(ids):
                    break
                identifiers.update(ids)
                pages.append(page)
                if len(identifiers) == expected:
                    return [pages]
            if attempt < 2:
                pause(5 * (attempt + 1))
        raise ValueError("workflow catalog changed or truncated on three reads")

    result = sliced(lower, upper)
    identifiers = [row["id"] for query in result for page in query for row in page["workflow_runs"]]
    if len(identifiers) != len(set(identifiers)):
        raise ValueError("workflow catalog contains overlapping slices")
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--endpoint", required=True)
    parser.add_argument("--lower", required=True)
    parser.add_argument("--upper", required=True)
    parser.add_argument("--maximum", required=True, type=int)
    parser.add_argument("--event")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    queries = collect(args.endpoint, timestamp(args.lower), timestamp(args.upper),
                      args.maximum, event=args.event)
    args.output.mkdir(parents=True, exist_ok=True)
    for old in args.output.glob("*.json"):
        old.unlink()
    for query, pages in enumerate(queries, 1):
        for number, page in enumerate(pages, 1):
            (args.output / f"q{query:03d}-{number:03d}.json").write_text(json.dumps(page))
    print(f"catalog={args.endpoint} slices={len(queries)} "
          f"runs={sum(page['total_count'] for pages in queries for page in pages[:1])}")


if __name__ == "__main__":
    main()
