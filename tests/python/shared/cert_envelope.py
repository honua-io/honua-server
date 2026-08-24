# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Shared certification-envelope writer for canonical-client interop lanes.

The PyQGIS lane grew its own copy of this collector first (see
``tests/python/pyqgis/conftest.py``). The canonical analyst lanes added by
[#3392](https://github.com/honua-io/honua-server/issues/3392) — GeoPandas,
OWSLib, DuckDB Spatial, R sf/ows4R, and pystac-client — all need the same
envelope shape, the same status-precedence rule, and the same receipt
bindings, so the logic lives here instead of being copied five more times.

The envelope schema is defined by
``docs/gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md``. Two fields are
load-bearing for the certification gate:

``fixture_revision`` / ``server_config_revision``
    Content-addressed digests of the fixture and server-configuration
    inputs, required by ``fixturePolicy.requiredReceiptFields`` in
    ``docs/gis/data/client-certification-matrix.v1.json``. A lane that
    cannot bind both is not allowed to claim canonical-fixture provenance.

Applicability is explicit rather than inferred: a lane declares which
common-core IDs it substantiates and which are structurally
``not-applicable`` for its protocol surface. Anything applicable that the
run did not execute is emitted as ``skip``, which the strict baseline diff
treats as a fail-closed signal rather than a pass.
"""

from __future__ import annotations

import datetime
import hashlib
import json
import os
import subprocess
from dataclasses import dataclass
from pathlib import Path

import httpx


# The 24-ID common core (18 base + 6 visual/style slice IDs). Kept in sync
# with scripts/client-compat/convert-gdal-results.py and
# tests/js-browser/cesium/support/cert-reporter.ts.
COMMON_CORE_IDS: tuple[str, ...] = (
    "CERT-CONN-01", "CERT-CONN-02",
    "CERT-AUTH-01", "CERT-AUTH-02",
    "CERT-DISC-01", "CERT-DISC-02",
    "CERT-SCHM-01", "CERT-SCHM-02",
    "CERT-QFLT-01", "CERT-QFLT-02",
    "CERT-PAGE-01", "CERT-PAGE-02",
    "CERT-GEOM-01", "CERT-GEOM-02",
    "CERT-ERRH-01", "CERT-ERRH-02",
    "CERT-RNDR-01", "CERT-RNDR-02",
    "CERT-RNDR-SYM-01", "CERT-RNDR-LIN-01", "CERT-RNDR-FIL-01",
    "CERT-RNDR-LBL-01", "CERT-RNDR-SPR-01", "CERT-RNDR-URL-01",
)

# Rendering facets. Data clients (GeoPandas, DuckDB, R sf, pystac-client)
# have no drawing surface at all, so these are structurally not-applicable
# rather than "not exercised yet".
RENDERING_IDS: frozenset[str] = frozenset({
    "CERT-RNDR-01", "CERT-RNDR-02",
    "CERT-RNDR-SYM-01", "CERT-RNDR-LIN-01", "CERT-RNDR-FIL-01",
    "CERT-RNDR-LBL-01", "CERT-RNDR-SPR-01", "CERT-RNDR-URL-01",
})

_STATUS_RANK = {"fail": 3, "pass": 2, "skip": 1, "not-applicable": 1}

# Geometry tolerance thresholds from CROSS_CLIENT_CERTIFICATION_MATRIX.md.
GEOGRAPHIC_TOLERANCE_DEGREES = 1e-6
PROJECTED_TOLERANCE_METERS = 0.01


@dataclass
class CertResult:
    """One CERT-* observation inside an evidence envelope."""

    test_case_id: str
    status: str
    duration_ms: int | None = None
    measured_count: int | None = None
    measured_delta: float | None = None
    notes: str = ""
    evidence_ref: str = ""


@dataclass(frozen=True)
class LaneRuntime:
    """Receipt bindings every envelope this lane emits must carry."""

    base_url: str
    environment: str
    server_version: str
    server_commit: str
    fixture_revision: str
    server_config_revision: str


class CertificationEvidenceCollector:
    """Accumulates CERT-* results and writes one ``.cert.json`` per protocol.

    ``applicable`` is the set of common-core IDs this lane is contractually
    required to substantiate. Every other common-core ID is emitted as
    ``not-applicable`` with ``not_applicable_reason`` recorded in notes, so
    the envelope always carries the full 24-ID vocabulary and a reader can
    tell "structurally impossible" apart from "did not run".
    """

    def __init__(
        self,
        runtime: LaneRuntime,
        *,
        client_lane: str,
        client_version: str,
        protocol: str,
        protocol_version: str,
        applicable: frozenset[str] | set[str],
        not_applicable_reason: str,
    ) -> None:
        unknown = set(applicable) - set(COMMON_CORE_IDS)
        if unknown:
            raise ValueError(
                f"{client_lane}/{protocol} declares unknown common-core IDs: {sorted(unknown)}"
            )
        self.runtime = runtime
        self.client_lane = client_lane
        self.client_version = client_version
        self.protocol = protocol
        self.protocol_version = protocol_version
        self.applicable = frozenset(applicable)
        self.not_applicable_reason = not_applicable_reason
        self._results: dict[str, CertResult] = {}
        self._extensions: dict[str, CertResult] = {}

    # -- recording ---------------------------------------------------------

    def record(
        self,
        test_case_id: str,
        status: str,
        *,
        duration_ms: int | None = None,
        measured_count: int | None = None,
        measured_delta: float | None = None,
        notes: str = "",
        evidence_ref: str = "",
    ) -> None:
        """Record one observation, worst-status-wins.

        Ties break toward the richer record so a generic post-run hook
        cannot erase measured counts/deltas a test body already captured.
        """
        if status not in _STATUS_RANK:
            raise ValueError(f"unknown status {status!r} for {test_case_id}")
        if test_case_id in COMMON_CORE_IDS and test_case_id not in self.applicable:
            raise ValueError(
                f"{self.client_lane}/{self.protocol} recorded {test_case_id}, "
                "which it declares not-applicable; fix the applicability set "
                "or stop recording the case."
            )
        candidate = CertResult(
            test_case_id=test_case_id,
            status=status,
            duration_ms=duration_ms,
            measured_count=measured_count,
            measured_delta=measured_delta,
            notes=notes,
            evidence_ref=evidence_ref,
        )
        bucket = self._results if test_case_id in COMMON_CORE_IDS else self._extensions
        existing = bucket.get(test_case_id)
        if existing is None or _prefer(candidate, existing):
            bucket[test_case_id] = candidate

    def try_record(self, test_case_id: str, status: str, **kwargs) -> bool:
        """Record only if this lane declares the case applicable.

        ``record`` deliberately raises when a lane writes a case it declared
        not-applicable — that is a contract bug worth failing on. But the
        generic ``pytest_runtest_makereport`` hook fires for every test,
        including ones whose ``cert`` marker belongs to a sibling protocol's
        collector, so the hook path needs a non-throwing variant. Returns
        whether the record was taken.
        """
        if test_case_id in COMMON_CORE_IDS and test_case_id not in self.applicable:
            return False
        self.record(test_case_id, status, **kwargs)
        return True

    # -- output ------------------------------------------------------------

    def build_envelope(self) -> dict:
        results: list[dict] = []
        for case_id in COMMON_CORE_IDS:
            if case_id in self.applicable:
                recorded = self._results.get(case_id)
                if recorded is None:
                    # Fail closed: an applicable case the run never reached
                    # is a gap, not a pass.
                    results.append(_as_dict(CertResult(
                        case_id,
                        "skip",
                        notes="Applicable to this lane but not executed in this run.",
                    )))
                else:
                    results.append(_as_dict(recorded))
            else:
                results.append(_as_dict(CertResult(
                    case_id, "not-applicable", notes=self.not_applicable_reason
                )))

        # docs/gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md: `summary` aggregates
        # the `results` array only. Extension results are tracked separately in
        # `extensions` so a lane cannot inflate its common-core pass count with
        # lane-specific cases.
        statuses = [entry["status"] for entry in results]
        return {
            "schema_version": "1.0",
            "run_id": utc_now_compact(),
            "run_date": utc_now_iso(),
            "server_version": self.runtime.server_version,
            "server_commit": self.runtime.server_commit,
            "fixture_revision": self.runtime.fixture_revision,
            "server_config_revision": self.runtime.server_config_revision,
            "client_lane": self.client_lane,
            "client_version": self.client_version,
            "protocol": self.protocol,
            "protocol_version": self.protocol_version,
            "environment": self.runtime.environment,
            "results": results,
            "summary": {
                "total": len(statuses),
                "passed": sum(1 for value in statuses if value == "pass"),
                "failed": sum(1 for value in statuses if value == "fail"),
                "skipped": sum(1 for value in statuses if value == "skip"),
                "not_applicable": sum(1 for value in statuses if value == "not-applicable"),
            },
            "cite_results": None,
            "extensions": [_as_dict(entry) for entry in self._extensions.values()],
        }

    def write_envelope(self, path: Path) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(self.build_envelope(), indent=2) + "\n")

    @property
    def has_records(self) -> bool:
        return bool(self._results) or bool(self._extensions)


def _prefer(candidate: CertResult, existing: CertResult) -> bool:
    candidate_rank = _STATUS_RANK.get(candidate.status, 0)
    existing_rank = _STATUS_RANK.get(existing.status, 0)
    if candidate_rank != existing_rank:
        return candidate_rank > existing_rank
    return _richness(candidate) > _richness(existing)


def _richness(result: CertResult) -> int:
    score = 0
    if result.measured_count is not None:
        score += 4
    if result.measured_delta is not None:
        score += 2
    if result.evidence_ref:
        score += 2
    if result.notes:
        score += 1
    if result.duration_ms is not None:
        score += 1
    return score


def _as_dict(result: CertResult) -> dict:
    return {
        "test_case_id": result.test_case_id,
        "status": result.status,
        "duration_ms": result.duration_ms,
        "measured_count": result.measured_count,
        "measured_delta": result.measured_delta,
        "notes": result.notes,
        "evidence_ref": result.evidence_ref,
    }


# ---------------------------------------------------------------------------
# Receipt binding helpers
# ---------------------------------------------------------------------------

def utc_now_iso() -> str:
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def utc_now_compact() -> str:
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")


def file_digest(path: Path) -> str:
    return f"sha256:{hashlib.sha256(path.read_bytes()).hexdigest()}"


def read_server_version(
    base_url: str,
    *,
    override_env: str = "",
    api_key: str | None = None,
) -> str:
    """Resolve the server version for the receipt.

    ``/api/v1/admin/version`` is part of the control plane and answers 401 to an
    anonymous caller, so probing it without a key silently recorded
    ``server_version: "unknown"`` on every lane's envelope — a receipt that
    cannot name the build it certified. The client-compat admin key is passed
    by default; callers running against a stack with a different key override
    it, and a lane can still pin the value through ``override_env``.
    """
    configured = os.getenv(override_env) if override_env else None
    if configured:
        return configured
    headers = {}
    key = api_key if api_key is not None else os.getenv("HONUA_ADMIN_API_KEY")
    if key:
        headers["X-API-Key"] = key
    try:
        response = httpx.get(
            f"{base_url}/api/v1/admin/version", timeout=15.0, headers=headers
        )
        response.raise_for_status()
        payload = response.json()
        return payload.get("data", {}).get("version") or payload.get("version") or "unknown"
    except (httpx.HTTPError, ValueError, TypeError):
        return "unknown"


def read_server_commit(project_root: Path, *, override_env: str = "") -> str:
    configured = os.getenv(override_env) if override_env else None
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


def build_lane_runtime(
    *,
    base_url: str,
    project_root: Path,
    fixture_path: Path,
    server_config_path: Path,
    version_env: str = "",
    commit_env: str = "",
    api_key: str | None = None,
) -> LaneRuntime:
    """Assemble the receipt bindings shared by every canonical-client lane."""
    normalized = base_url.rstrip("/")
    return LaneRuntime(
        base_url=normalized,
        environment="ci" if os.getenv("CI") else "local",
        server_version=read_server_version(
            normalized, override_env=version_env, api_key=api_key
        ),
        server_commit=read_server_commit(project_root, override_env=commit_env),
        fixture_revision=file_digest(fixture_path),
        server_config_revision=file_digest(server_config_path),
    )
