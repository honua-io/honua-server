# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Fixtures and certification-evidence wiring for the GeoPandas client lane.

The lane certifies the server against the stack a working analyst actually
installs: ``geopandas`` driving ``pyogrio`` (vendored GDAL) with ``fiona`` as
the cross-engine control. Every feature read in this lane goes through a real
GeoPandas/pyogrio call — ``geopandas.read_file``, ``pyogrio.read_dataframe``,
``pyogrio.read_info``, ``pyogrio.list_layers`` — never a hand-rolled HTTP
feature read. ``httpx`` is used only for the control-plane auth probe
(``CERT-AUTH-01/02``), which has no client-library surface at all.

Two envelopes are emitted per run, one per protocol:

* ``{run_id}-py-geopandas-ogc-features.cert.json``
* ``{run_id}-py-geopandas-wfs.cert.json``

The lane is container-only. It never starts a server: ``HONUA_GEOPANDAS_BASE_URL``
(or ``HONUA_BASE_URL``) must point at a running instance, otherwise the session
fails loudly rather than silently degrading to a local-server fallback.
"""

from __future__ import annotations

import os
import time
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any, Generator
from urllib.request import urlopen

import pytest
from _pytest.reports import TestReport

from shared import canonical_fixture, cert_envelope
from shared.cert_envelope import CertificationEvidenceCollector, LaneRuntime

# ---------------------------------------------------------------------------
# Lane contract
# ---------------------------------------------------------------------------

CLIENT_LANE = "py-geopandas"

OGC_FEATURES_PROTOCOL = "ogc-features"
OGC_FEATURES_PROTOCOL_VERSION = "1.0"
WFS_PROTOCOL = "wfs"
WFS_PROTOCOL_VERSION = "2.0.0"

#: The 16 common-core IDs this lane substantiates on both protocols.
APPLICABLE_CASES = frozenset({
    "CERT-CONN-01", "CERT-CONN-02",
    "CERT-AUTH-01", "CERT-AUTH-02",
    "CERT-DISC-01", "CERT-DISC-02",
    "CERT-SCHM-01", "CERT-SCHM-02",
    "CERT-QFLT-01", "CERT-QFLT-02",
    "CERT-PAGE-01", "CERT-PAGE-02",
    "CERT-GEOM-01", "CERT-GEOM-02",
    "CERT-ERRH-01", "CERT-ERRH-02",
})

NOT_APPLICABLE_REASON = (
    "GeoPandas is a data-access client with no drawing surface; rendering "
    "facets are structurally not applicable."
)

DEFAULT_OUTPUT_DIR = canonical_fixture.TESTS_ROOT / "TestResults"


# ---------------------------------------------------------------------------
# Environment
# ---------------------------------------------------------------------------

def _resolve_base_url() -> str:
    base_url = (
        os.getenv("HONUA_GEOPANDAS_BASE_URL")
        or os.getenv("HONUA_BASE_URL")
        or ""
    ).strip()
    if not base_url:
        pytest.fail(
            "The GeoPandas certification lane is container-only and never "
            "starts its own server. Set HONUA_GEOPANDAS_BASE_URL (or "
            "HONUA_BASE_URL) to a running Honua instance, e.g. "
            "http://honua:5000 on the docker/client-compat compose network."
        )
    return base_url.rstrip("/")


def resolve_client_version() -> str:
    """Return the resolved client stack, e.g. ``geopandas=1.0.1;...;GDAL=3.9.1``.

    Computed at runtime from the installed distributions so the envelope
    records what actually ran rather than what the requirements file asked for.
    """
    parts: list[str] = []
    try:
        import geopandas

        parts.append(f"geopandas={geopandas.__version__}")
    except ImportError:  # pragma: no cover - the lane cannot run without it
        parts.append("geopandas=unavailable")
    try:
        import pyogrio

        parts.append(f"pyogrio={pyogrio.__version__}")
        gdal_version = getattr(pyogrio, "__gdal_version_string__", None)
    except ImportError:  # pragma: no cover
        parts.append("pyogrio=unavailable")
        gdal_version = None
    try:
        import fiona

        parts.append(f"fiona={fiona.__version__}")
    except ImportError:  # pragma: no cover
        parts.append("fiona=unavailable")
    parts.append(f"GDAL={gdal_version or 'unknown'}")
    return ";".join(parts)


# ---------------------------------------------------------------------------
# Module-level collectors (written once at session teardown)
# ---------------------------------------------------------------------------

_ogc_features_evidence: CertificationEvidenceCollector | None = None
_wfs_evidence: CertificationEvidenceCollector | None = None


def _build_collector(
    runtime: LaneRuntime,
    protocol: str,
    protocol_version: str,
) -> CertificationEvidenceCollector:
    return CertificationEvidenceCollector(
        runtime,
        client_lane=CLIENT_LANE,
        client_version=resolve_client_version(),
        protocol=protocol,
        protocol_version=protocol_version,
        applicable=APPLICABLE_CASES,
        not_applicable_reason=NOT_APPLICABLE_REASON,
    )


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------

@pytest.fixture(autouse=True)
def reset_worker_state() -> None:
    """Shadow the shared worker reset fixture with a no-op.

    ``tests/python/conftest.py`` declares an autouse ``reset_worker_state``
    that transitively starts a PostGIS Testcontainer. This lane targets an
    already-running server and must never start one.
    """


@pytest.fixture(scope="session")
def lane_runtime() -> LaneRuntime:
    """Receipt bindings shared by both envelopes this lane emits."""
    return cert_envelope.build_lane_runtime(
        base_url=_resolve_base_url(),
        project_root=canonical_fixture.PROJECT_ROOT,
        fixture_path=canonical_fixture.SEED_PATH,
        server_config_path=canonical_fixture.SERVER_CONFIG_PATH,
        version_env="HONUA_GEOPANDAS_SERVER_VERSION",
        commit_env="HONUA_GEOPANDAS_SERVER_COMMIT",
    )


@pytest.fixture(scope="session")
def base_url(lane_runtime: LaneRuntime) -> str:
    """Base URL of the Honua instance under certification."""
    return lane_runtime.base_url


@pytest.fixture(scope="session")
def geopandas_service_id() -> str:
    """Service id the lane certifies against."""
    return os.getenv("HONUA_GEOPANDAS_SERVICE_ID", canonical_fixture.SERVICE_ID)


@pytest.fixture(scope="session")
def geopandas_collection_id() -> str:
    """OGC API Features collection id the lane certifies against."""
    return os.getenv(
        "HONUA_GEOPANDAS_COLLECTION_ID", canonical_fixture.COLLECTION_ID
    )


@pytest.fixture(scope="session")
def oapif_dsn(base_url: str) -> str:
    """GDAL OAPIF dataset name (the same DSN shape the gdal lane uses)."""
    return f"OAPIF:{base_url}/ogc/features"


@pytest.fixture(scope="session")
def items_url(base_url: str, geopandas_collection_id: str) -> str:
    """Direct ``/items`` URL, used for OGC query parameters GDAL cannot express.

    ``geopandas.read_file(url)`` fetches the URL and hands the payload to
    pyogrio's GeoJSON driver, so this is still a real client read - it simply
    lets the lane exercise OGC API Features query parameters (``datetime``,
    ``filter``, ``sortby``, ``crs``, ``bbox-crs``, ``offset``) that the OAPIF
    driver never emits on its own.
    """
    return f"{base_url}/ogc/features/collections/{geopandas_collection_id}/items"


@pytest.fixture(scope="session")
def wfs_dsn(base_url: str) -> str:
    """GDAL WFS dataset name (GetCapabilities URL, as the gdal lane uses)."""
    return f"{WFS_PROTOCOL.upper()}:{wfs_capabilities_url(base_url)}"


@pytest.fixture(scope="session")
def wfs_getfeature_url(base_url: str, wfs_typename: str) -> str:
    """Direct WFS 2.0 GetFeature URL for the certified type name."""
    return (
        f"{base_url}/wfs?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetFeature"
        f"&TYPENAMES={wfs_typename}"
    )


@pytest.fixture(scope="session")
def wfs_typename(base_url: str) -> str:
    """Discover the certified WFS type name from GetCapabilities.

    A missing or empty capabilities document is a hard failure: this lane is
    contractually required to substantiate the WFS protocol, so a silent skip
    would launder a regression into a green run.
    """
    name = _discover_wfs_typename(base_url)
    if name is None:
        pytest.fail(
            "WFS GetCapabilities at "
            f"{wfs_capabilities_url(base_url)} returned no FeatureType "
            "entries (endpoint missing, unreachable, or empty). The "
            "py-geopandas lane certifies WFS 2.0.0 and cannot proceed."
        )
    return name


@pytest.fixture(scope="session")
def ogc_features_evidence(lane_runtime: LaneRuntime) -> CertificationEvidenceCollector:
    """Session-scoped OGC API Features evidence collector."""
    global _ogc_features_evidence
    if _ogc_features_evidence is None:
        _ogc_features_evidence = _build_collector(
            lane_runtime, OGC_FEATURES_PROTOCOL, OGC_FEATURES_PROTOCOL_VERSION
        )
    return _ogc_features_evidence


@pytest.fixture(scope="session")
def wfs_evidence(lane_runtime: LaneRuntime) -> CertificationEvidenceCollector:
    """Session-scoped WFS evidence collector."""
    global _wfs_evidence
    if _wfs_evidence is None:
        _wfs_evidence = _build_collector(
            lane_runtime, WFS_PROTOCOL, WFS_PROTOCOL_VERSION
        )
    return _wfs_evidence


@pytest.fixture(scope="session", autouse=True)
def _write_cert_evidence(
    ogc_features_evidence: CertificationEvidenceCollector,
    wfs_evidence: CertificationEvidenceCollector,
) -> Generator[None, None, None]:
    """Persist both ``.cert.json`` envelopes at session teardown.

    ``HONUA_GEOPANDAS_OUTPUT_DIR`` overrides the destination; the container
    lane points it at ``/output`` because ``tests/`` is bind-mounted read-only.
    """
    yield
    override = os.environ.get("HONUA_GEOPANDAS_OUTPUT_DIR")
    results_dir = Path(override) if override else DEFAULT_OUTPUT_DIR
    run_id = cert_envelope.utc_now_compact()

    for collector, protocol in (
        (ogc_features_evidence, OGC_FEATURES_PROTOCOL),
        (wfs_evidence, WFS_PROTOCOL),
    ):
        if not collector.has_records:
            continue
        collector.write_envelope(
            results_dir / f"{run_id}-{CLIENT_LANE}-{protocol}.cert.json"
        )


# ---------------------------------------------------------------------------
# Result recording helpers
# ---------------------------------------------------------------------------

class CaseTimer:
    """Wall-clock stopwatch for the ``duration_ms`` envelope field."""

    def __init__(self) -> None:
        self._started = time.perf_counter()

    @property
    def elapsed_ms(self) -> int:
        """Milliseconds elapsed since construction."""
        return int((time.perf_counter() - self._started) * 1000)


def record_pass(
    collector: CertificationEvidenceCollector,
    case_id: str,
    timer: CaseTimer,
    *,
    measured_count: int | None = None,
    measured_delta: float | None = None,
    notes: str = "",
    evidence_ref: str = "",
) -> None:
    """Record a passing observation with its measurement and timing."""
    collector.record(
        case_id,
        "pass",
        duration_ms=timer.elapsed_ms,
        measured_count=measured_count,
        measured_delta=measured_delta,
        notes=notes,
        evidence_ref=evidence_ref,
    )


def record_fail(
    collector: CertificationEvidenceCollector,
    case_id: str,
    timer: CaseTimer,
    *,
    measured_count: int | None = None,
    measured_delta: float | None = None,
    notes: str = "",
) -> None:
    """Record a diagnosed failure without aborting the surrounding test."""
    collector.record(
        case_id,
        "fail",
        duration_ms=timer.elapsed_ms,
        measured_count=measured_count,
        measured_delta=measured_delta,
        notes=notes,
    )


# ---------------------------------------------------------------------------
# WFS capabilities helpers
# ---------------------------------------------------------------------------

def wfs_capabilities_url(base_url: str) -> str:
    """Return the WFS 2.0.0 GetCapabilities URL for ``base_url``."""
    return f"{base_url}/wfs?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetCapabilities"


def _discover_wfs_typename(base_url: str) -> str | None:
    """Parse GetCapabilities and return the first advertised type name."""
    try:
        with urlopen(wfs_capabilities_url(base_url), timeout=30) as response:  # noqa: S310
            tree = ET.parse(response)
    except Exception:  # noqa: BLE001 - any transport/parse failure means "no name"
        return None

    root = tree.getroot()
    namespaces = {
        "wfs": "http://www.opengis.net/wfs/2.0",
        "ows": "http://www.opengis.net/ows/1.1",
    }
    for feature_type in root.findall(".//wfs:FeatureType", namespaces):
        name = feature_type.find("wfs:Name", namespaces)
        if name is not None and name.text:
            return name.text.strip()
    for element in root.iter():
        if element.tag.endswith("FeatureType"):
            for child in element:
                if child.tag.endswith("Name") and child.text:
                    return child.text.strip()
    return None


# ---------------------------------------------------------------------------
# Shared assertion helpers
# ---------------------------------------------------------------------------

def row_identities(frame: Any) -> set[Any]:
    """Return a stable per-row identity set for disjointness assertions.

    Prefers the fixture's feature-id field, then the unique ``uid``/``name``
    columns, and finally the frame index (which the paging reads populate with
    real OGR FIDs via ``fid_as_index=True``).
    """
    for column in (canonical_fixture.FEATURE_ID_FIELD, "uid", "name"):
        if column in frame.columns:
            values = frame[column]
            if values.notna().all() and values.nunique() == len(frame):
                return set(values.tolist())
    return set(frame.index.tolist())


def http_status_of(exc: BaseException) -> int | None:
    """Extract an HTTP status code from a client-raised exception, if present."""
    code = getattr(exc, "code", None)
    if isinstance(code, int):
        return code
    for token in ("400", "401", "403", "404", "500"):
        if token in str(exc):
            return int(token)
    return None


# ---------------------------------------------------------------------------
# Hook: unexpected failures and skips must still land in the envelope
# ---------------------------------------------------------------------------

@pytest.hookimpl(hookwrapper=True)
def pytest_runtest_makereport(item: pytest.Item, call):  # noqa: ANN001, ANN201
    """Turn uncaught failures/skips into recorded ``fail``/``skip`` results."""
    outcome = yield
    report: TestReport = outcome.get_result()

    collector = _collector_for_item(item)
    if collector is None:
        return

    case_id = _extract_cert_id(item)
    if case_id is None:
        return

    if report.failed and report.when in {"setup", "call"}:
        collector.try_record(
            case_id,
            "fail",
            notes=(report.longreprtext or "")[:500],
        )
    elif report.skipped:
        collector.try_record(
            case_id,
            "skip",
            notes=str(report.longrepr)[:500] if report.longrepr else "",
        )


def _collector_for_item(item: pytest.Item) -> CertificationEvidenceCollector | None:
    module = Path(str(item.fspath)).stem
    if "ogc_features" in module:
        return _ogc_features_evidence
    if "wfs" in module:
        return _wfs_evidence
    return None


def _extract_cert_id(item: pytest.Item) -> str | None:
    marker = item.get_closest_marker("cert")
    if marker and marker.args:
        return str(marker.args[0])
    return None
