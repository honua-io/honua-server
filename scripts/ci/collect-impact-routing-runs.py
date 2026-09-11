#!/usr/bin/env python3
"""Collect impact-routing evidence inside one bounded GITHUB_TOKEN request budget.

Run catalogs are read in complete time slices below GitHub's 1,000-result
ceiling. Artifact catalogs come from name-filtered repository listings; only a
successful run that lists no current-attempt receipt costs a per-run listing.
Receipt archives are immutable per artifact ID and carry a SHA-256 digest, so a
restored cache supplies every archive an earlier audit already verified.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import itertools
import json
import subprocess
import sys
import time
import zipfile
from datetime import datetime, timezone
from pathlib import Path

LISTING_PAGE = 100
DOWNLOAD_ATTEMPTS = 4


def timestamp(value: str) -> int:
    return int(datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp())


def iso(value: int) -> str:
    return datetime.fromtimestamp(value, timezone.utc).isoformat().replace("+00:00", "Z")


class BudgetExhausted(RuntimeError):
    """Raised before a request that the job-wide allowance cannot admit."""


class RequestBudget:
    """GITHUB_TOKEN requests the audit job may still make, shared by every step.

    GitHub meters the token per repository per hour, so the allowance is the
    policy limit less its reserve, clamped to what `/rate_limit` (which is not
    itself metered) says is left. Every attempt, retries included, is charged
    before it is sent, so the job can never spend past the allowance.
    """

    def __init__(self, path: Path):
        self.path = path

    @classmethod
    def create(cls, path: Path, limit: int, reserve: int, remaining: int) -> "RequestBudget":
        if limit < 1 or not 0 <= reserve < limit or remaining < 0:
            raise ValueError("invalid request budget")
        allowance = min(limit, remaining) - reserve
        if allowance < 1:
            raise BudgetExhausted(
                f"only {remaining} GITHUB_TOKEN requests remain this hour; "
                f"the {reserve}-request reserve leaves nothing for the audit")
        budget = cls(path)
        budget._write({"allowance": allowance, "used": 0, "spent": {}})
        return budget

    def state(self) -> dict:
        return json.loads(self.path.read_text(encoding="utf-8"))

    def _write(self, state: dict) -> None:
        partial = self.path.with_name(self.path.name + ".part")
        partial.write_text(json.dumps(state, sort_keys=True), encoding="utf-8")
        partial.replace(self.path)

    def spend(self, label: str, count: int = 1) -> None:
        state = self.state()
        if state["used"] + count > state["allowance"]:
            raise BudgetExhausted(
                f"GITHUB_TOKEN request budget exhausted during {label}: "
                f"{state['used']}/{state['allowance']} requests used, {count} more needed")
        state["used"] += count
        state["spent"][label] = state["spent"].get(label, 0) + count
        self._write(state)

    def charge(self, label: str):
        return lambda: self.spend(label)


def _gh(command: list[str], *, text: bool, spend) -> str | bytes:
    deadline = time.monotonic() + 300
    for delay in (10, 30, 60, 120, None):
        spend()
        try:
            result = subprocess.run(command, capture_output=True, text=text,
                                    timeout=max(1, min(90, deadline - time.monotonic())))
            if result.returncode == 0:
                return result.stdout
            error = result.stderr if text else result.stderr.decode("utf-8", "replace")
            error = error.strip()
        except subprocess.TimeoutExpired:
            error = "GitHub request timed out"
        transient = any(message in error.lower() for message in (
            "error connecting", "could not resolve host", "connection reset",
            "timeout", "timed out", "http 502", "http 503", "http 504",
        ))
        # A forbidden response is not a transport error. Back off within the
        # same bound, then fail visibly; never change authentication.
        forbidden = "403" in error
        remaining = deadline - time.monotonic()
        if not (transient or forbidden) or delay is None or remaining <= delay:
            raise RuntimeError(error)
        time.sleep(delay)
    raise AssertionError("unreachable")


def github_page(endpoint: str, parameters: dict, spend=lambda: None) -> dict:
    command = ["gh", "api", "--method", "GET", endpoint]
    for key, value in parameters.items():
        command.extend(["-f", f"{key}={value}"])
    return json.loads(_gh(command, text=True, spend=spend))


def github_bytes(endpoint: str, spend=lambda: None) -> bytes:
    return _gh(["gh", "api", endpoint], text=False, spend=spend)


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


def read_runs(root: Path) -> list[dict]:
    return [run for path in sorted(root.glob("*.json"))
            for run in json.loads(path.read_text(encoding="utf-8"))["workflow_runs"]]


def _listing_rows(page: dict) -> list[dict]:
    rows = page.get("artifacts")
    if not isinstance(rows, list) or not all(
        isinstance(row, dict)
        and type(row.get("id")) is int
        and isinstance(row.get("name"), str)
        and isinstance(row.get("created_at"), str)
        and isinstance(row.get("workflow_run"), dict)
        for row in rows
    ):
        raise ValueError("invalid artifact listing page")
    return rows


def collect_artifacts(repository: str, runs: list[dict], names: list[str], *,
                      fetch) -> tuple[dict[int, dict], dict]:
    """Build one artifact catalog per successful run from name-filtered listings.

    A per-run listing costs one request for every successful observer (~1,740
    in seven days). The repository listing filtered by the exact receipt name
    returns the same artifact objects 100 at a time, newest first, so paging
    stops once a page reaches artifacts older than the oldest run. Receipt
    names are attempt-qualified, so each attempt present in the run catalog is
    listed. Skip markers carry open-ended codes and cannot be name-listed:
    every run that lists no current-attempt receipt - a skip, a loss, or a
    receipt a paging race displaced - gets its full per-run catalog instead,
    exactly as before. Ordering only affects cost, never completeness.
    """
    if not names:
        raise ValueError("at least one receipt name is required")
    successful = {run["id"]: run for run in runs
                  if run.get("status") == "completed" and run.get("conclusion") == "success"}
    listed: dict[int, dict[int, dict]] = {run_id: {} for run_id in successful}
    listing_requests = 0
    if successful:
        oldest = min(timestamp(run["created_at"]) for run in successful.values())
        for name, attempt in itertools.product(
                names, sorted({run["run_attempt"] for run in successful.values()})):
            for number in itertools.count(1):
                rows = _listing_rows(fetch(f"repos/{repository}/actions/artifacts", {
                    "name": f"{name}-attempt-{attempt}", "per_page": LISTING_PAGE,
                    "page": number}))
                listing_requests += 1
                for row in rows:
                    catalog = listed.get(row["workflow_run"].get("id"))
                    if catalog is not None:
                        catalog.setdefault(row["id"], row)
                if len(rows) < LISTING_PAGE or timestamp(rows[-1]["created_at"]) < oldest:
                    break
    fallback = sorted(
        run_id for run_id, rows in listed.items()
        if not any(row.get("expired") is False
                   and row["name"] in {f"{name}-attempt-{successful[run_id]['run_attempt']}"
                                       for name in names}
                   for row in rows.values()))
    catalogs = {
        run_id: {"total_count": len(rows),
                 "artifacts": [rows[key] for key in sorted(rows)]}
        for run_id, rows in listed.items()
    }
    for run_id in fallback:
        catalogs[run_id] = fetch(f"repos/{repository}/actions/runs/{run_id}/artifacts",
                                 {"per_page": LISTING_PAGE})
    return catalogs, {
        "successful_runs": len(successful),
        "listing_requests": listing_requests,
        "per_run_catalogs": len(fallback),
    }


def read_digests(roots: list[Path]) -> dict[int, str]:
    digests: dict[int, str] = {}
    for root in roots:
        for path in sorted(root.glob("*.json")):
            for row in json.loads(path.read_text(encoding="utf-8"))["artifacts"]:
                digest = row.get("digest")
                if isinstance(digest, str) and digest.startswith("sha256:"):
                    digests[row["id"]] = digest.removeprefix("sha256:")
    return digests


def verified_archive(body: bytes, digest: str | None) -> bool:
    # Older artifacts predate GitHub's digest field; a readable zip is all that
    # can be checked for them, as before.
    if digest is not None and hashlib.sha256(body).hexdigest() != digest:
        return False
    return zipfile.is_zipfile(io.BytesIO(body))


def download_receipts(repository: str, index_path: Path, archives: Path,
                      digests: dict[int, str], maximum: int, *, fetch_bytes, fetch,
                      expire, pause=time.sleep) -> dict:
    """Download only indexed receipts the restored archive cache lacks.

    An artifact ID names immutable bytes, and a cached archive is reused only
    when it still matches the catalog digest. Archives outside the current
    index are pruned so the cache tracks the retention window.
    """
    identifiers = [entry["artifact_id"]
                   for entry in json.loads(index_path.read_text(encoding="utf-8"))["artifacts"]]
    if not all(type(value) is int and value > 0 for value in identifiers):
        raise ValueError("index artifact id is invalid")
    if len(identifiers) > maximum:
        raise ValueError(
            f"{len(identifiers)} indexed receipts exceed the {maximum} download budget. "
            "Raise maximum_receipt_downloads in .github/impact-routing-promotion.json "
            "or shorten receipt_retention_days.")
    archives.mkdir(parents=True, exist_ok=True)
    wanted = {f"{value}.zip" for value in identifiers}
    for path in archives.iterdir():
        if path.name not in wanted:
            path.unlink()
    stats = {"indexed": len(identifiers), "cached": 0, "downloaded": 0, "expired": 0}
    for artifact_id in identifiers:
        destination = archives / f"{artifact_id}.zip"
        digest = digests.get(artifact_id)
        if destination.is_file() and verified_archive(destination.read_bytes(), digest):
            stats["cached"] += 1
            continue
        destination.unlink(missing_ok=True)
        invalid_archive = False
        for attempt in range(1, DOWNLOAD_ATTEMPTS + 1):
            try:
                body = fetch_bytes(f"repos/{repository}/actions/artifacts/{artifact_id}/zip")
            except BudgetExhausted:
                raise
            except RuntimeError:
                body = None
            if body is not None:
                if verified_archive(body, digest):
                    partial = destination.with_name(destination.name + ".part")
                    partial.write_bytes(body)
                    partial.replace(destination)
                    stats["downloaded"] += 1
                    break
                invalid_archive = True
            if attempt < DOWNLOAD_ATTEMPTS:
                pause(attempt * 2)
        else:
            expired = None
            if not invalid_archive:
                try:
                    expired = fetch(f"repos/{repository}/actions/artifacts/{artifact_id}",
                                    {}).get("expired")
                except BudgetExhausted:
                    raise
                except RuntimeError:
                    pass
            if expired is True:
                expire(artifact_id)
                stats["expired"] += 1
                print(f"::warning::receipt artifact {artifact_id} expired after discovery; "
                      "counted as owed receipt loss", file=sys.stderr)
                continue
            raise RuntimeError(f"receipt artifact {artifact_id} was unavailable or invalid "
                               f"after {DOWNLOAD_ATTEMPTS} attempts")
    return stats


def budget_report(state: dict) -> str:
    phases = ", ".join(f"{label} `{count}`" for label, count in sorted(state["spent"].items()))
    return (f"GITHUB_TOKEN requests: `{state['used']}` of `{state['allowance']}` allowed"
            f" ({phases or 'none'}).\n")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    budget_parser = commands.add_parser("budget")
    budget_parser.add_argument("--output", required=True, type=Path)
    budget_parser.add_argument("--limit", required=True, type=int)
    budget_parser.add_argument("--reserve", required=True, type=int)
    runs_parser = commands.add_parser("runs")
    runs_parser.add_argument("--budget", required=True, type=Path)
    runs_parser.add_argument("--endpoint", required=True)
    runs_parser.add_argument("--lower", required=True)
    runs_parser.add_argument("--upper", required=True)
    runs_parser.add_argument("--maximum", required=True, type=int)
    runs_parser.add_argument("--event")
    runs_parser.add_argument("--output", required=True, type=Path)
    artifacts_parser = commands.add_parser("artifacts")
    artifacts_parser.add_argument("--budget", required=True, type=Path)
    artifacts_parser.add_argument("--repository", required=True)
    artifacts_parser.add_argument("--runs", required=True, type=Path)
    artifacts_parser.add_argument("--name", required=True, action="append")
    artifacts_parser.add_argument("--output", required=True, type=Path)
    download_parser = commands.add_parser("download")
    download_parser.add_argument("--budget", required=True, type=Path)
    download_parser.add_argument("--repository", required=True)
    download_parser.add_argument("--index", required=True, type=Path)
    download_parser.add_argument("--archives", required=True, type=Path)
    download_parser.add_argument("--catalogs", required=True, type=Path, action="append")
    download_parser.add_argument("--maximum", required=True, type=int)
    spend_parser = commands.add_parser("spend")
    spend_parser.add_argument("--budget", required=True, type=Path)
    spend_parser.add_argument("--label", required=True)
    spend_parser.add_argument("--requests", required=True, type=int)
    report_parser = commands.add_parser("report")
    report_parser.add_argument("--budget", required=True, type=Path)
    args = parser.parse_args()

    try:
        if args.command == "budget":
            remaining = github_page("rate_limit", {})["resources"]["core"]["remaining"]
            budget = RequestBudget.create(args.output, args.limit, args.reserve, remaining)
            print(f"request-budget allowance={budget.state()['allowance']} "
                  f"remaining={remaining} limit={args.limit} reserve={args.reserve}")
            return 0
        budget = RequestBudget(args.budget)
        if args.command == "report":
            sys.stdout.write(budget_report(budget.state()))
            return 0
        if args.command == "spend":
            budget.spend(args.label, args.requests)
            return 0
        if args.command == "runs":
            def fetch(endpoint, parameters):
                return github_page(endpoint, parameters, budget.charge("run-catalogs"))
            queries = collect(args.endpoint, timestamp(args.lower), timestamp(args.upper),
                              args.maximum, event=args.event, fetch=fetch)
            args.output.mkdir(parents=True, exist_ok=True)
            for old in args.output.glob("*.json"):
                old.unlink()
            for query, pages in enumerate(queries, 1):
                for number, page in enumerate(pages, 1):
                    (args.output / f"q{query:03d}-{number:03d}.json").write_text(json.dumps(page))
            print(f"catalog={args.endpoint} slices={len(queries)} "
                  f"runs={sum(page['total_count'] for pages in queries for page in pages[:1])}")
        elif args.command == "artifacts":
            def fetch(endpoint, parameters):
                return github_page(endpoint, parameters, budget.charge("artifact-catalogs"))
            catalogs, stats = collect_artifacts(args.repository, read_runs(args.runs),
                                                args.name, fetch=fetch)
            args.output.mkdir(parents=True, exist_ok=True)
            for old in args.output.glob("*.json"):
                old.unlink()
            for run_id, catalog in catalogs.items():
                (args.output / f"{run_id}.json").write_text(json.dumps(catalog))
            print("artifact-catalogs " + " ".join(f"{key}={value}" for key, value in stats.items()))
        else:
            audit = Path(__file__).with_name("audit-impact-routing-evidence.py")
            stats = download_receipts(
                args.repository, args.index, args.archives, read_digests(args.catalogs),
                args.maximum,
                fetch_bytes=lambda endpoint: github_bytes(endpoint, budget.charge("downloads")),
                fetch=lambda endpoint, parameters: github_page(
                    endpoint, parameters, budget.charge("downloads")),
                expire=lambda artifact_id: subprocess.run(
                    [sys.executable, str(audit), "expire-indexed-receipt",
                     "--index", str(args.index), "--artifact-id", str(artifact_id)],
                    check=True),
            )
            print("receipts " + " ".join(f"{key}={value}" for key, value in stats.items()))
    except BudgetExhausted as error:
        # Archives already written stay in place for the cache save, so the
        # next audit resumes from them instead of repeating the transfers.
        print(f"::error::{error}. Verified archives are cached; the next audit resumes "
              "from them.", file=sys.stderr)
        return 1
    finally:
        if args.command not in ("budget", "report") and args.budget.is_file():
            print(budget_report(budget.state()).strip())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
