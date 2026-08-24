# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""Fixtures and certification-evidence wiring for the OWSLib client lane.

Shape mirrors ``tests/python/pyqgis/conftest.py``: session-scoped collectors,
a ``pytest_runtest_makereport`` hookwrapper so a crashing test still lands in
the envelope, and a session-teardown writer. The envelope schema, applicability
semantics, and receipt bindings all come from ``shared.cert_envelope``.

The lane never starts a server. ``HONUA_OWSLIB_BASE_URL`` (or ``HONUA_BASE_URL``)
must point at a running Honua instance; anything else is a hard failure rather
than a silent skip, because a certification lane that quietly declines to run is
indistinguishable from a passing one in the baseline diff.
"""

from __future__ import annotations

import math
import os
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Generator, Iterator

import pytest
import requests
from _pytest.reports import TestReport
from owslib import __version__ as OWSLIB_VERSION
from owslib.util import Authentication, http_get

from shared import canonical_fixture, cert_envelope
from shared.cert_envelope import CertificationEvidenceCollector, LaneRuntime

CLIENT_LANE = "py-owslib"

# ---------------------------------------------------------------------------
# Applicability contracts
# ---------------------------------------------------------------------------

# Vector protocols. OWSLib hands back parsed features/GML for these, so every
# non-rendering common-core facet is in scope.
VECTOR_APPLICABLE = frozenset({
    "CERT-CONN-01", "CERT-CONN-02",
    "CERT-AUTH-01", "CERT-AUTH-02",
    "CERT-DISC-01", "CERT-DISC-02",
    "CERT-SCHM-01", "CERT-SCHM-02",
    "CERT-QFLT-01", "CERT-QFLT-02",
    "CERT-PAGE-01", "CERT-PAGE-02",
    "CERT-GEOM-01", "CERT-GEOM-02",
    "CERT-ERRH-01", "CERT-ERRH-02",
})
VECTOR_NA_REASON = (
    "OWSLib returns parsed feature data for this protocol and has no drawing "
    "surface; rendering facets are structurally not applicable."
)

# Raster protocols. The server renders; OWSLib only consumes the image, so the
# feature-shaped facets have no observable surface at all.
RASTER_APPLICABLE = frozenset({
    "CERT-CONN-01", "CERT-CONN-02",
    "CERT-AUTH-01", "CERT-AUTH-02",
    "CERT-DISC-01", "CERT-DISC-02",
    "CERT-ERRH-01",
    "CERT-RNDR-01",
    "CERT-GEOM-02",
})
RASTER_NA_REASON = (
    "OWSLib consumes server-rendered imagery for this protocol; feature schema, "
    "attribute/spatial query, pagination, and per-feature geometry fidelity "
    "facets are structurally not applicable."
)

PROTOCOL_SPECS: dict[str, tuple[str, frozenset[str], str]] = {
    # protocol -> (protocol_version, applicable, not_applicable_reason)
    "ogc-features": ("1.0", VECTOR_APPLICABLE, VECTOR_NA_REASON),
    "wfs": ("2.0.0", VECTOR_APPLICABLE, VECTOR_NA_REASON),
    "wms": ("1.3.0", RASTER_APPLICABLE, RASTER_NA_REASON),
    "wmts": ("1.0.0", RASTER_APPLICABLE, RASTER_NA_REASON),
}

# Test module stem -> protocol. Used both by the collector fixtures and by the
# makereport hook, so a module can never report into the wrong envelope.
MODULE_PROTOCOL: dict[str, str] = {
    "test_ogc_features": "ogc-features",
    "test_wfs": "wfs",
    "test_wms": "wms",
    "test_wmts": "wmts",
}

TESTS_ROOT = canonical_fixture.TESTS_ROOT

_collectors: dict[str, CertificationEvidenceCollector] = {}


# ---------------------------------------------------------------------------
# Environment
# ---------------------------------------------------------------------------

def _require_base_url() -> str:
    base = os.getenv("HONUA_OWSLIB_BASE_URL") or os.getenv("HONUA_BASE_URL")
    if not base:
        raise RuntimeError(
            "The OWSLib certification lane targets an already-running Honua "
            "server. Set HONUA_OWSLIB_BASE_URL (or HONUA_BASE_URL) to its base "
            "URL. This lane deliberately has no local-server fallback: silently "
            "skipping would publish an all-skip envelope that the baseline diff "
            "cannot tell apart from a real run."
        )
    return base.rstrip("/")


@dataclass(frozen=True)
class LaneConfig:
    """Fixture identifiers this lane certifies against."""

    base_url: str
    #: Vector fixture (tests/seed/client-compat-v1.sql).
    service_id: str
    collection_id: str
    #: Raster fixture (tests/seed/browser-compat.yaml) used by WMS/WMTS.
    raster_service_id: str
    raster_layer_id: str

    @property
    def wms_url(self) -> str:
        return f"{self.base_url}/rest/services/{self.raster_service_id}/MapServer/WMS"

    @property
    def wmts_url(self) -> str:
        return f"{self.base_url}/rest/services/{self.raster_service_id}/MapServer/WMTS"

    @property
    def wfs_url(self) -> str:
        return f"{self.base_url}/wfs"

    @property
    def oaf_url(self) -> str:
        return f"{self.base_url}/ogc/features"


# ---------------------------------------------------------------------------
# Session fixtures
# ---------------------------------------------------------------------------

@pytest.fixture(autouse=True)
def reset_worker_state() -> None:
    """Shadow the shared worker reset fixture with a no-op.

    ``tests/python/conftest.py`` declares an autouse ``reset_worker_state`` that
    depends on a live PostGIS Testcontainer. This lane targets an already-running
    server and must never start one.
    """


@pytest.fixture(scope="session")
def lane_config() -> LaneConfig:
    return LaneConfig(
        base_url=_require_base_url(),
        service_id=os.getenv("HONUA_OWSLIB_SERVICE_ID", canonical_fixture.SERVICE_ID),
        collection_id=os.getenv("HONUA_OWSLIB_COLLECTION_ID", canonical_fixture.COLLECTION_ID),
        raster_service_id=os.getenv("HONUA_OWSLIB_RASTER_SERVICE_ID", "browser_compat"),
        raster_layer_id=os.getenv("HONUA_OWSLIB_RASTER_LAYER_ID", "2000"),
    )


@pytest.fixture(scope="session")
def base_url(lane_config: LaneConfig) -> str:
    return lane_config.base_url


@pytest.fixture(scope="session")
def owslib_version() -> str:
    return OWSLIB_VERSION


def _resolve_server_version(base_url: str) -> None:
    """Populate ``HONUA_OWSLIB_SERVER_VERSION`` from the admin version endpoint.

    ``cert_envelope.read_server_version`` probes ``/api/v1/admin/version``
    anonymously, which the control plane answers with 401, so an unassisted lane
    records ``server_version: unknown`` and the envelope loses a receipt field.
    The lane already holds the admin API key for CERT-AUTH-02, so it resolves the
    version here and hands it to the shared helper through the override env var
    the helper offers. An explicit override from CI always wins.
    """
    if os.getenv("HONUA_OWSLIB_SERVER_VERSION"):
        return
    try:
        response = http_get(
            f"{base_url}/api/v1/admin/version",
            headers={canonical_fixture.ADMIN_API_KEY_HEADER: canonical_fixture.ADMIN_API_KEY},
            timeout=30,
        )
        if response.ok:
            payload = response.json()
            version = payload.get("data", {}).get("version") or payload.get("version")
            if version:
                os.environ["HONUA_OWSLIB_SERVER_VERSION"] = str(version)
    except (requests.RequestException, ValueError, TypeError):
        # Leave the field to the shared helper, which records "unknown".
        pass


@pytest.fixture(scope="session")
def lane_runtime(lane_config: LaneConfig) -> LaneRuntime:
    _resolve_server_version(lane_config.base_url)
    return cert_envelope.build_lane_runtime(
        base_url=lane_config.base_url,
        project_root=canonical_fixture.PROJECT_ROOT,
        fixture_path=canonical_fixture.SEED_PATH,
        server_config_path=canonical_fixture.SERVER_CONFIG_PATH,
        version_env="HONUA_OWSLIB_SERVER_VERSION",
        commit_env="HONUA_OWSLIB_SERVER_COMMIT",
    )


def _collector(runtime: LaneRuntime, client_version: str, protocol: str) -> CertificationEvidenceCollector:
    existing = _collectors.get(protocol)
    if existing is not None:
        return existing
    protocol_version, applicable, reason = PROTOCOL_SPECS[protocol]
    created = CertificationEvidenceCollector(
        runtime,
        client_lane=CLIENT_LANE,
        client_version=client_version,
        protocol=protocol,
        protocol_version=protocol_version,
        applicable=applicable,
        not_applicable_reason=reason,
    )
    _collectors[protocol] = created
    return created


@pytest.fixture(scope="session")
def oaf_collector(lane_runtime: LaneRuntime, owslib_version: str) -> CertificationEvidenceCollector:
    return _collector(lane_runtime, owslib_version, "ogc-features")


@pytest.fixture(scope="session")
def wfs_collector(lane_runtime: LaneRuntime, owslib_version: str) -> CertificationEvidenceCollector:
    return _collector(lane_runtime, owslib_version, "wfs")


@pytest.fixture(scope="session")
def wms_collector(lane_runtime: LaneRuntime, owslib_version: str) -> CertificationEvidenceCollector:
    return _collector(lane_runtime, owslib_version, "wms")


@pytest.fixture(scope="session")
def wmts_collector(lane_runtime: LaneRuntime, owslib_version: str) -> CertificationEvidenceCollector:
    return _collector(lane_runtime, owslib_version, "wmts")


# ---------------------------------------------------------------------------
# Control-plane (CERT-AUTH-*) probe
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class AdminProbe:
    """Result of resolving the admin control-plane authentication scheme.

    The data protocols in the client-compat fixture are anonymous, so
    CERT-AUTH-01/02 are substantiated against the admin control plane. Honua
    authenticates it with an API key (``X-API-Key: $HONUA_ADMIN_PASSWORD``), not
    HTTP Basic and not a bearer login flow -- see
    ``src/Honua.Hosting/Features/Authentication/ApiKeyAuthenticationHandler.cs``.
    HTTP Basic compatibility exists but is off by default *and* HTTPS-gated, so
    it cannot work on the plain-HTTP compose network.
    """

    scheme: str
    anonymous_status: int
    authenticated_status: int
    challenge: str
    attempts: tuple[tuple[str, int], ...]


def _admin_url(base_url: str) -> str:
    return f"{base_url}{canonical_fixture.ADMIN_PROBE_PATH}"


def admin_get(base_url: str, *, headers: dict[str, str] | None = None,
              auth: Authentication | None = None) -> requests.Response:
    """GET the admin probe path through OWSLib's own HTTP surface.

    ``owslib.util.http_get`` is the function every ``owslib.ogcapi`` client uses
    internally, and it honours ``owslib.util.Authentication`` (including its
    ``auth_delegate`` escape hatch). Using it here keeps the control-plane probe
    on the client under certification rather than a side-channel HTTP library,
    and unlike ``owslib.util.openURL`` it surfaces the raw status code instead of
    raising, which is exactly what CERT-AUTH-01 needs to observe.
    """
    return http_get(
        _admin_url(base_url),
        headers=headers or {},
        auth=auth or Authentication(),
        timeout=30,
    )


@pytest.fixture(scope="session")
def admin_probe(base_url: str) -> AdminProbe:
    """Resolve, once per session, which admin auth scheme the server accepts."""
    anonymous = admin_get(base_url)
    challenge = anonymous.headers.get("WWW-Authenticate", "")

    attempts: list[tuple[str, int]] = []

    api_key = admin_get(
        base_url,
        headers={canonical_fixture.ADMIN_API_KEY_HEADER: canonical_fixture.ADMIN_API_KEY},
    )
    attempts.append(("x-api-key", api_key.status_code))
    if api_key.ok:
        return AdminProbe("x-api-key", anonymous.status_code, api_key.status_code,
                          challenge, tuple(attempts))

    basic = admin_get(
        base_url,
        auth=Authentication(canonical_fixture.ADMIN_USERNAME, canonical_fixture.ADMIN_PASSWORD),
    )
    attempts.append(("http-basic", basic.status_code))
    if basic.ok:
        return AdminProbe("http-basic", anonymous.status_code, basic.status_code,
                          challenge, tuple(attempts))

    bearer = admin_get(
        base_url,
        headers={"Authorization": f"Bearer {canonical_fixture.ADMIN_API_KEY}"},
    )
    attempts.append(("bearer", bearer.status_code))
    if bearer.ok:
        return AdminProbe("bearer", anonymous.status_code, bearer.status_code,
                          challenge, tuple(attempts))

    raise AssertionError(
        "No admin authentication scheme was accepted by "
        f"{_admin_url(base_url)}. Observed: anonymous={anonymous.status_code} "
        f"challenge={challenge!r}; " + ", ".join(f"{name}={code}" for name, code in attempts)
    )


# ---------------------------------------------------------------------------
# Recording helpers
# ---------------------------------------------------------------------------

class Timer:
    """Millisecond stopwatch for the ``duration_ms`` envelope field."""

    def __init__(self) -> None:
        self._start = time.perf_counter()

    @property
    def ms(self) -> int:
        return int((time.perf_counter() - self._start) * 1000)


@pytest.fixture
def timer() -> Timer:
    return Timer()


def geographic_delta(observed: tuple[float, float], expected: tuple[float, float]) -> float:
    """Maximum absolute per-ordinate deviation, in the CRS's native unit."""
    return max(abs(observed[0] - expected[0]), abs(observed[1] - expected[1]))


def web_mercator(lon: float, lat: float) -> tuple[float, float]:
    """Project WGS84 lon/lat to EPSG:3857 metres (spherical Mercator)."""
    radius = 20037508.342789244
    x = lon * radius / 180.0
    y = math.log(math.tan((90.0 + lat) * math.pi / 360.0)) / (math.pi / 180.0)
    return x, y * radius / 180.0


def strip_crs_brackets(value: str | None) -> str:
    """Normalise a ``Content-Crs`` header value (RFC 8288 angle brackets)."""
    return (value or "").strip().strip("<>").strip()


# ---------------------------------------------------------------------------
# Evidence teardown + failure capture
# ---------------------------------------------------------------------------

@pytest.fixture(scope="session", autouse=True)
def _write_cert_evidence(
    oaf_collector: CertificationEvidenceCollector,
    wfs_collector: CertificationEvidenceCollector,
    wms_collector: CertificationEvidenceCollector,
    wmts_collector: CertificationEvidenceCollector,
) -> Generator[None, None, None]:
    """Persist one ``.cert.json`` envelope per protocol at session teardown.

    ``HONUA_OWSLIB_OUTPUT_DIR`` overrides the destination -- the compose lane
    points it at ``/output`` because ``tests/`` is bind-mounted read-only.
    ``run_id`` uses ``cert_envelope.utc_now_compact()``, which contains no ``-``,
    so ``scripts/client-compat/refresh-baselines.sh`` can strip the prefix up to
    the first ``-`` and promote the file under its stable name.
    """
    yield
    override = os.environ.get("HONUA_OWSLIB_OUTPUT_DIR")
    results_dir = Path(override) if override else TESTS_ROOT / "TestResults"
    results_dir.mkdir(parents=True, exist_ok=True)
    run_id = cert_envelope.utc_now_compact()
    worker_id = os.environ.get("PYTEST_XDIST_WORKER")
    suffix = "" if not worker_id or worker_id == "master" else f"-{worker_id}"

    for protocol, collector in (
        ("ogc-features", oaf_collector),
        ("wfs", wfs_collector),
        ("wms", wms_collector),
        ("wmts", wmts_collector),
    ):
        path = results_dir / f"{run_id}-{CLIENT_LANE}-{protocol}{suffix}.cert.json"
        collector.write_envelope(path)


@pytest.hookimpl(hookwrapper=True)
def pytest_runtest_makereport(item: pytest.Item, call: Any) -> Iterator[None]:
    """Record unexpected failures and skips onto the owning protocol collector.

    Without this, a case that blows up before its body could call
    ``collector.record`` would silently degrade to the collector's
    fail-closed ``skip``, losing the reason. The shared collector's
    worst-status-wins rule means this can only ever make an envelope more
    pessimistic, never less.
    """
    outcome = yield
    report: TestReport = outcome.get_result()

    protocol = MODULE_PROTOCOL.get(Path(str(item.fspath)).stem)
    collector = _collectors.get(protocol) if protocol else None
    if collector is None:
        return

    marker = item.get_closest_marker("cert")
    if not marker or not marker.args:
        return
    case_ids = [str(arg) for arg in marker.args]

    if report.when == "call" and report.failed:
        status, notes = "fail", (report.longreprtext or "")[:500]
    elif report.skipped:
        status, notes = "skip", (str(report.longrepr)[:500] if report.longrepr else "")
    elif report.when == "setup" and report.failed:
        status, notes = "fail", f"setup error: {(report.longreprtext or '')[:460]}"
    else:
        return

    for case_id in case_ids:
        # try_record, not record: the raster collectors declare only 9 of the
        # 24 common-core IDs applicable, and the strict `record` raises on a
        # mismatch. That strictness is right inside a test body but would take
        # the whole session down from a generic hook.
        collector.try_record(case_id, status, notes=notes)
