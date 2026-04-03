# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Fixtures and evidence reporting for STAC client compatibility tests.
"""

from __future__ import annotations

import datetime
import importlib.metadata
import json
import os
import subprocess
from dataclasses import dataclass
from pathlib import Path
from typing import Generator

import httpx
import pytest
from _pytest.reports import TestReport

from shared.postgis import PostGISFixture
from shared.server import HonuaServer


PROJECT_ROOT = Path(__file__).resolve().parents[3]
TESTS_ROOT = Path(__file__).resolve().parents[2]
PYTHON_TESTS_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SEED_PATH = TESTS_ROOT / "seed" / "client-compat-v1.sql"
DEFAULT_SERVICE_ID = "test_service"
DEFAULT_COLLECTION_ID = "0"
DEFAULT_PORT = 5565
DEFAULT_TIMEOUT_SECONDS = 120


def _get_worker_id() -> str:
    return os.getenv("PYTEST_XDIST_WORKER", "gw0")


def _get_worker_index(worker_id: str) -> int:
    if worker_id.startswith("gw") and worker_id[2:].isdigit():
        return int(worker_id[2:])
    return 0


@dataclass(frozen=True)
class StacCompatibilityRuntime:
    """Runtime metadata for the STAC compatibility lane."""

    base_url: str
    stac_url: str
    mode: str
    service_id: str
    collection_id: str
    seed_snapshot_name: str
    seed_snapshot_path: str
    server_version: str
    server_commit: str


@dataclass(frozen=True)
class _EvidenceRecord:
    test_name: str
    summary: str
    status: str
    detail: str = ""


class CompatibilityEvidenceCollector:
    """Accumulates structured STAC compatibility results."""

    def __init__(self, runtime: StacCompatibilityRuntime) -> None:
        self.runtime = runtime
        self._records: list[_EvidenceRecord] = []
        self._client_versions = {
            "pystac": _safe_package_version("pystac"),
            "pystac_client": _safe_package_version("pystac-client"),
        }

    def record(
        self,
        test_name: str,
        summary: str,
        status: str,
        detail: str = "",
    ) -> None:
        self._records.append(
            _EvidenceRecord(
                test_name=test_name,
                summary=summary,
                status=status,
                detail=detail.strip(),
            )
        )

    def write_reports(self, json_path: Path, markdown_path: Path) -> None:
        report = self._build_report()
        json_path.write_text(json.dumps(report, indent=2))
        markdown_path.write_text(self._build_markdown(report))

    def _build_report(self) -> dict[str, object]:
        statuses = [record.status for record in self._records]
        overall_status = "fail" if "fail" in statuses else "skip" if "skip" in statuses else "pass"

        return {
            "schema_version": "1.0",
            "lane": "stac-client-compat",
            "generated_at": _utc_now_iso(),
            "status": overall_status,
            "base_url": self.runtime.base_url,
            "stac_url": self.runtime.stac_url,
            "mode": self.runtime.mode,
            "service_id": self.runtime.service_id,
            "collection_id": self.runtime.collection_id,
            "seed_snapshot": self.runtime.seed_snapshot_name,
            "seed_snapshot_path": self.runtime.seed_snapshot_path,
            "server_version": self.runtime.server_version,
            "server_commit": self.runtime.server_commit,
            "client_versions": self._client_versions,
            "summary": {
                "total": len(self._records),
                "passed": sum(1 for record in self._records if record.status == "pass"),
                "failed": sum(1 for record in self._records if record.status == "fail"),
                "skipped": sum(1 for record in self._records if record.status == "skip"),
            },
            "checks": [
                {
                    "test": record.test_name,
                    "summary": record.summary,
                    "status": record.status,
                    "detail": record.detail,
                }
                for record in self._records
            ],
        }

    def _build_markdown(self, report: dict[str, object]) -> str:
        summary = report["summary"]
        checks = report["checks"]
        summary_data = summary if isinstance(summary, dict) else {}
        check_rows = checks if isinstance(checks, list) else []

        lines = [
            "# STAC Client Compatibility Evidence",
            "",
            f"- Generated at: {report['generated_at']}",
            f"- Status: {report['status']}",
            f"- Base URL: {report['base_url']}",
            f"- STAC URL: {report['stac_url']}",
            f"- Mode: {report['mode']}",
            f"- Service ID: {report['service_id']}",
            f"- Collection ID: {report['collection_id']}",
            f"- Seed snapshot: {report['seed_snapshot']}",
            f"- Seed snapshot path: {report['seed_snapshot_path']}",
            f"- Server version: {report['server_version']}",
            f"- Server commit: {report['server_commit']}",
            f"- PySTAC version: {self._client_versions['pystac']}",
            f"- PySTAC-Client version: {self._client_versions['pystac_client']}",
            "",
            "## Summary",
            "",
            f"- Total checks: {summary_data.get('total', 0)}",
            f"- Passed: {summary_data.get('passed', 0)}",
            f"- Failed: {summary_data.get('failed', 0)}",
            f"- Skipped: {summary_data.get('skipped', 0)}",
            "",
            "## Checks",
            "",
            "| Test | Summary | Status | Detail |",
            "| --- | --- | --- | --- |",
        ]

        for check in check_rows:
            if not isinstance(check, dict):
                continue
            lines.append(
                f"| { _markdown_cell(check.get('test')) } "
                f"| { _markdown_cell(check.get('summary')) } "
                f"| { _markdown_cell(check.get('status')) } "
                f"| { _markdown_cell(check.get('detail')) } |"
            )

        return "\n".join(lines) + "\n"

    @property
    def has_records(self) -> bool:
        return len(self._records) > 0


_evidence: CompatibilityEvidenceCollector | None = None


def _safe_package_version(name: str) -> str:
    try:
        return importlib.metadata.version(name)
    except importlib.metadata.PackageNotFoundError:
        return "unknown"


def _markdown_cell(value: object) -> str:
    return str(value or "").replace("\n", "<br>").replace("|", "\\|")


def _utc_now_iso() -> str:
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def _read_server_version(base_url: str) -> str:
    configured = os.getenv("HONUA_STAC_COMPAT_SERVER_VERSION")
    if configured:
        return configured

    try:
        response = httpx.get(f"{base_url}/api/v1/admin/version", timeout=15.0)
        response.raise_for_status()
        payload = response.json()
        return (
            payload.get("data", {}).get("version")
            or payload.get("version")
            or "unknown"
        )
    except (httpx.HTTPError, ValueError, TypeError):
        return "unknown"


def _read_server_commit(project_root: Path) -> str:
    configured = os.getenv("HONUA_STAC_COMPAT_SERVER_COMMIT")
    if configured:
        return configured

    try:
        result = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            cwd=project_root,
            check=True,
            capture_output=True,
            text=True,
            timeout=10.0,
        )
        return result.stdout.strip() or "unknown"
    except (FileNotFoundError, subprocess.CalledProcessError, subprocess.TimeoutExpired):
        return "unknown"


def _build_runtime(
    *,
    base_url: str,
    mode: str,
    service_id: str,
    collection_id: str,
    seed_snapshot_name: str,
    seed_snapshot_path: str,
) -> StacCompatibilityRuntime:
    normalized_base_url = base_url.rstrip("/")
    server_commit = _read_server_commit(PROJECT_ROOT) if mode == "local" else (
        os.getenv("HONUA_STAC_COMPAT_SERVER_COMMIT") or "unknown"
    )
    return StacCompatibilityRuntime(
        base_url=normalized_base_url,
        stac_url=f"{normalized_base_url}/stac",
        mode=mode,
        service_id=service_id,
        collection_id=collection_id,
        seed_snapshot_name=seed_snapshot_name,
        seed_snapshot_path=seed_snapshot_path,
        server_version=_read_server_version(normalized_base_url),
        server_commit=server_commit,
    )


@pytest.fixture(autouse=True)
def reset_worker_state() -> None:
    """Shadow the shared worker reset fixture with a no-op."""


@pytest.fixture(scope="session")
def stac_runtime() -> Generator[StacCompatibilityRuntime, None, None]:
    """Start a dedicated STAC compatibility server or target an external base URL."""
    base_url_override = os.getenv("HONUA_STAC_COMPAT_BASE_URL")
    service_id = os.getenv("HONUA_STAC_COMPAT_SERVICE_ID", DEFAULT_SERVICE_ID)
    collection_id = os.getenv("HONUA_STAC_COMPAT_COLLECTION_ID", DEFAULT_COLLECTION_ID)
    seed_path = Path(os.getenv("HONUA_STAC_COMPAT_SEED_PATH", str(DEFAULT_SEED_PATH)))
    seed_snapshot_name = os.getenv("HONUA_STAC_COMPAT_SEED_SNAPSHOT", seed_path.name)

    if base_url_override:
        yield _build_runtime(
            base_url=base_url_override,
            mode="external",
            service_id=service_id,
            collection_id=collection_id,
            seed_snapshot_name=seed_snapshot_name,
            seed_snapshot_path=str(seed_path),
        )
        return

    fixture = PostGISFixture()
    fixture.start()
    fixture.apply_sql_file(seed_path)

    worker_id = _get_worker_id()
    port = int(os.getenv("HONUA_STAC_COMPAT_PORT", str(DEFAULT_PORT))) + _get_worker_index(worker_id)
    server = HonuaServer(
        connection_string=fixture.get_npgsql_connection_string(),
        port=port,
        project_root=PROJECT_ROOT,
    )
    server.start(timeout=float(os.getenv("HONUA_STAC_COMPAT_TIMEOUT", str(DEFAULT_TIMEOUT_SECONDS))))

    try:
        yield _build_runtime(
            base_url=server.base_url,
            mode="local",
            service_id=service_id,
            collection_id=collection_id,
            seed_snapshot_name=seed_snapshot_name,
            seed_snapshot_path=str(seed_path),
        )
    finally:
        server.stop()
        fixture.stop()


@pytest.fixture(scope="session")
def base_url(stac_runtime: StacCompatibilityRuntime) -> str:
    """Expose the dedicated STAC compatibility base URL."""
    return stac_runtime.base_url


@pytest.fixture(scope="session")
def stac_api_url(stac_runtime: StacCompatibilityRuntime) -> str:
    """Return the STAC landing-page URL."""
    return stac_runtime.stac_url


@pytest.fixture(scope="session")
def test_service_id(stac_runtime: StacCompatibilityRuntime) -> str:
    """Service ID seeded by the compatibility snapshot."""
    return stac_runtime.service_id


@pytest.fixture(scope="session")
def test_collection_id(stac_runtime: StacCompatibilityRuntime) -> str:
    """Collection ID seeded by the compatibility snapshot."""
    return stac_runtime.collection_id


@pytest.fixture(scope="session")
def stac_http_client(base_url: str) -> Generator[httpx.Client, None, None]:
    """HTTP client for raw STAC document validation."""
    with httpx.Client(
        base_url=base_url,
        timeout=30.0,
        headers={"Accept": "application/json"},
    ) as client:
        yield client


@pytest.fixture(scope="session")
def evidence_collector(
    stac_runtime: StacCompatibilityRuntime,
) -> CompatibilityEvidenceCollector:
    """Session-scoped evidence collector."""
    global _evidence
    if _evidence is None:
        _evidence = CompatibilityEvidenceCollector(stac_runtime)
    return _evidence


@pytest.fixture(scope="session", autouse=True)
def _write_evidence_report(
    evidence_collector: CompatibilityEvidenceCollector,
) -> Generator[None, None, None]:
    """Persist JSON and Markdown evidence at session teardown."""
    yield
    if not evidence_collector.has_records:
        return

    worker_id = os.environ.get("PYTEST_XDIST_WORKER")
    suffix = "" if not worker_id or worker_id == "master" else f"-{worker_id}"
    results_dir = TESTS_ROOT / "TestResults"
    results_dir.mkdir(parents=True, exist_ok=True)
    json_path = results_dir / f"stac-client-compat-results{suffix}.json"
    markdown_path = results_dir / f"stac-client-compat-results{suffix}.md"
    evidence_collector.write_reports(json_path, markdown_path)


@pytest.hookimpl(hookwrapper=True)
def pytest_runtest_makereport(item: pytest.Item, call):
    """Record failures and skips so evidence survives regressions."""
    outcome = yield
    report: TestReport = outcome.get_result()

    if _evidence is None:
        return

    if report.when == "call" and report.failed:
        _evidence.record(
            item.name,
            "STAC compatibility assertion failed",
            "fail",
            detail=(report.longreprtext or "")[:1000],
        )
    elif report.skipped:
        _evidence.record(
            item.name,
            "STAC compatibility test skipped",
            "skip",
            detail=str(report.longrepr) if report.longrepr else "",
        )
