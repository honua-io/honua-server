# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
GDAL/OGR interoperability test fixtures.

Provides skip logic, connection-string helpers, a subprocess wrapper,
and a session-scoped evidence collector that writes a structured JSON
report at session teardown.
"""

from __future__ import annotations

import datetime
import json
import os
import shutil
import subprocess
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

import pytest
from _pytest.reports import TestReport


# ============================================================================
# Structured result from an OGR CLI invocation
# ============================================================================


@dataclass(frozen=True)
class OgrResult:
    """Structured result from an OGR CLI command."""

    command: list[str]
    returncode: int
    stdout: str
    stderr: str

    @property
    def succeeded(self) -> bool:
        return self.returncode == 0

    def assert_success(self, msg: str = "") -> "OgrResult":
        """Assert the command exited 0; include full output on failure."""
        detail = msg or f"OGR command failed: {' '.join(self.command)}"
        assert self.succeeded, (
            f"{detail}\n"
            f"exit code: {self.returncode}\n"
            f"--- stdout ---\n{self.stdout}\n"
            f"--- stderr ---\n{self.stderr}"
        )
        return self


# ============================================================================
# Evidence collection
# ============================================================================


@dataclass
class _EvidenceRecord:
    test_name: str
    protocol: str
    category: str
    status: str
    detail: str = ""


class EvidenceCollector:
    """Accumulates per-test pass/fail/skip and writes a JSON report."""

    def __init__(self) -> None:
        self.gdal_version: str = ""
        self._records: list[_EvidenceRecord] = []

    def record(
        self,
        test_name: str,
        protocol: str,
        category: str,
        status: str,
        detail: str = "",
    ) -> None:
        self._records.append(
            _EvidenceRecord(
                test_name=test_name,
                protocol=protocol,
                category=category,
                status=status,
                detail=detail,
            )
        )

    def write_report(self, path: Path) -> None:
        protocols: dict[str, dict[str, str]] = {}
        for rec in self._records:
            bucket = protocols.setdefault(rec.protocol, {})
            existing = bucket.get(rec.category, "pass")
            if rec.status == "fail" or existing == "fail":
                bucket[rec.category] = "fail"
            elif rec.status == "skip" or existing == "skip":
                bucket[rec.category] = "skip"
            else:
                bucket[rec.category] = "pass"

        report = {
            "gdal_version": self.gdal_version,
            "timestamp": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "protocols": protocols,
            "details": [
                {
                    "test": r.test_name,
                    "protocol": r.protocol,
                    "category": r.category,
                    "status": r.status,
                    "detail": r.detail,
                }
                for r in self._records
            ],
        }
        path.write_text(json.dumps(report, indent=2))

    @property
    def has_records(self) -> bool:
        return len(self._records) > 0


# Singleton shared across the session
_evidence = EvidenceCollector()


# ============================================================================
# Helper to capture GDAL version
# ============================================================================


def _get_gdal_version() -> str | None:
    try:
        result = subprocess.run(
            ["ogrinfo", "--version"],
            capture_output=True,
            text=True,
            timeout=10,
        )
        if result.returncode == 0:
            return result.stdout.strip()
    except (FileNotFoundError, subprocess.TimeoutExpired):
        pass
    return None


# ============================================================================
# Fixtures
# ============================================================================


@pytest.fixture(scope="session", autouse=True)
def gdal_available():
    """Skip the entire gdal_ogr module when gdal-bin is not installed."""
    if shutil.which("ogrinfo") is None:
        pytest.skip(
            "GDAL tools not found \u2014 install gdal-bin to run "
            "GDAL/OGR interoperability tests."
        )
    version = _get_gdal_version()
    if version:
        _evidence.gdal_version = version


@pytest.fixture(scope="session")
def gdal_version() -> str:
    """Return the GDAL version string."""
    return _get_gdal_version() or "unknown"


@pytest.fixture(scope="session")
def oapif_dsn(base_url: str) -> str:
    """GDAL OAPIF driver connection string."""
    return f"OAPIF:{base_url}/ogc/features"


@pytest.fixture(scope="session")
def wfs_dsn(base_url: str) -> str:
    """GDAL WFS driver connection string."""
    return f"WFS:{base_url}/wfs?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetCapabilities"


@pytest.fixture
def ogr_run() -> Callable[..., OgrResult]:
    """Return a helper that runs an OGR CLI command with timeout."""

    def _run(args: list[str], timeout: float = 30.0) -> OgrResult:
        result = subprocess.run(
            args,
            capture_output=True,
            text=True,
            timeout=timeout,
        )
        return OgrResult(
            command=args,
            returncode=result.returncode,
            stdout=result.stdout,
            stderr=result.stderr,
        )

    return _run


@pytest.fixture(scope="session")
def evidence_collector() -> EvidenceCollector:
    """Session-scoped evidence collector for JSON report."""
    return _evidence


@pytest.fixture(scope="session", autouse=True)
def _write_evidence_report():
    """Write the evidence JSON report at session teardown."""
    yield
    if _evidence.has_records:
        worker_id = os.environ.get("PYTEST_XDIST_WORKER")
        suffix = "" if not worker_id or worker_id == "master" else f"-{worker_id}"
        report_path = Path(__file__).parent.parent / f"gdal-ogr-results{suffix}.json"
        _evidence.write_report(report_path)


# ============================================================================
# Hook: record failures and skips in the evidence collector
# ============================================================================


@pytest.hookimpl(hookwrapper=True)
def pytest_runtest_makereport(item: pytest.Item, call):
    """Automatically record test failures and skips in the evidence report.

    Inline ``evidence_collector.record(..., "pass")`` calls in individual
    tests handle the happy path.  This hook fills the gap so that failures
    and skips are never silently omitted from the JSON report.
    """
    outcome = yield
    report: TestReport = outcome.get_result()

    # Only act on the call phase (or setup for skips)
    if report.when == "call" and report.failed:
        protocol, category = _derive_protocol_category(item)
        _evidence.record(
            item.name, protocol, category, "fail",
            detail=(report.longreprtext or "")[:500],
        )
    elif report.skipped:
        protocol, category = _derive_protocol_category(item)
        _evidence.record(
            item.name, protocol, category, "skip",
            detail=str(report.longrepr) if report.longrepr else "",
        )


def _derive_protocol_category(item: pytest.Item) -> tuple[str, str]:
    """Derive (protocol, category) from a test item's module filename.

    Module naming convention: ``test_{protocol}_{category}.py``
    e.g. ``test_oapif_discovery.py`` → ``("oapif", "discovery")``.
    """
    stem = Path(item.fspath).stem  # "test_oapif_discovery"
    name = stem[5:] if stem.startswith("test_") else stem  # "oapif_discovery"
    tokens = name.split("_", 1)
    protocol = tokens[0] if tokens else "unknown"
    category = tokens[1] if len(tokens) > 1 else "general"
    return protocol, category


# ============================================================================
# Shared WFS helpers
# ============================================================================


def extract_first_layer_name(ogrinfo_output: str) -> str:
    """Extract the first layer name from ogrinfo listing output.

    ogrinfo output format::

        1: layer_name (Point)
        2: another_layer (Polygon)

    GDAL's WFS driver may also include the layer title before the
    geometry suffix, for example::

        1: honua:test_layer (title: Test Layer) (Point)
    """
    for line in ogrinfo_output.splitlines():
        line = line.strip()
        if line and line[0].isdigit() and ":" in line:
            after_colon = line.split(":", 1)[1].strip()
            if " (title:" in after_colon:
                after_colon = after_colon.split(" (title:", 1)[0].strip()
            elif after_colon.endswith(")") and " (" in after_colon:
                after_colon = after_colon.rsplit(" (", 1)[0].strip()

            return after_colon
    pytest.fail(f"Could not extract layer name from ogrinfo output:\n{ogrinfo_output}")


@pytest.fixture(scope="session")
def wfs_layer_name(wfs_dsn: str) -> str:
    """Discover and cache the first WFS layer name (session-scoped)."""
    result = subprocess.run(
        ["ogrinfo", wfs_dsn],
        capture_output=True,
        text=True,
        timeout=30,
    )
    assert result.returncode == 0, (
        f"WFS layer discovery failed:\n{result.stderr}"
    )
    return extract_first_layer_name(result.stdout)
