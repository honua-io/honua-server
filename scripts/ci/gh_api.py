"""Shared read-only `gh api` helpers for CI scripts: one retrying GET and one checked paginator.

Writes (POST, PATCH, dispatches) stay with their callers and are never retried here, because a
retry of a request GitHub had already accepted repeats its side effect.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time
from collections.abc import Callable, Iterator

DEFAULT_DELAYS = (10, 30, 60, 120)
PAGE_SIZE = 100


def gh_api(args: list[str], stdin: str | None = None) -> subprocess.CompletedProcess:
    """One `gh api` call with colour and TTY output disabled."""
    env = dict(os.environ, NO_COLOR="1", GH_FORCE_TTY="0")
    return subprocess.run(["gh", "api", *args], input=stdin, capture_output=True, text=True, env=env)


def failure_detail(result: subprocess.CompletedProcess) -> str:
    return " ".join((result.stderr or "").split())


def get_json(path: str, error: type[Exception] = RuntimeError,
             delays: tuple[int, ...] = DEFAULT_DELAYS) -> object:
    """GET `path` with backoff on transient failures; 404, 422, non-JSON or a persistent failure raise `error`."""
    for attempt in range(len(delays) + 1):
        result = gh_api([path])
        if result.returncode == 0:
            try:
                return json.loads(result.stdout)
            except json.JSONDecodeError as exc:
                raise error(f"gh api {path} returned non-JSON ({exc})") from exc
        detail = failure_detail(result)
        if attempt == len(delays) or "HTTP 404" in detail or "HTTP 422" in detail:
            raise error(f"gh api {path} failed: {detail}")
        print(f"::warning::gh api {path} failed ({detail}); retrying in {delays[attempt]}s", file=sys.stderr)
        time.sleep(delays[attempt])
    raise AssertionError("unreachable")


class Listing:
    """Paged GitHub listings read through `fetch`; malformed or truncated listings raise `error`."""

    def __init__(self, fetch: Callable[[str], object], error: type[Exception] = RuntimeError) -> None:
        self.fetch = fetch
        self.error = error

    def pages(self, path: str, key: str | None = None) -> Iterator[dict]:
        """Every row of a paged listing, newest page first.

        With `key` the response is an object holding the rows under `key`; its `total_count` must not
        move while paging and must match the rows seen, so a truncated listing raises.
        """
        separator = "&" if "?" in path else "?"
        page, seen, total = 1, 0, None
        while True:
            result = self.fetch(f"{path}{separator}per_page={PAGE_SIZE}&page={page}")
            rows, count = self._rows(result, f"{path} page {page}", key)
            if total is not None and count != total:
                raise self.error(f"{path} total_count moved from {total} to {count} while paging")
            total = count
            seen += len(rows)
            yield from rows
            if len(rows) < PAGE_SIZE:
                break
            page += 1
        if isinstance(total, int) and seen != total:
            raise self.error(f"{path} returned {seen} of {total} {key}; refusing a truncated listing")

    def _rows(self, result: object, label: str, key: str | None) -> tuple[list, object]:
        if not key:
            if not isinstance(result, list):
                raise self.error(f"{label} is not a list")
            return result, None
        if not isinstance(result, dict) or not isinstance(result.get(key), list):
            raise self.error(f"{label} has no {key} list")
        return result[key], result.get("total_count")
