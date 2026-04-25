# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Fixtures and certification evidence reporting for the PyQGIS compatibility lane.

Provides:
- Session-scoped autouse skip when ``qgis.core`` is unavailable.
- A headless ``QgsApplication`` fixture.
- Layer-construction helpers for OGC API Features and WFS.
- A certification-envelope evidence writer aligned with the
  ``CROSS_CLIENT_CERTIFICATION_EVIDENCE.md`` schema.
"""

from __future__ import annotations

import datetime
import json
import os
import subprocess
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import Generator
from urllib.request import urlopen

import httpx
import pytest
from _pytest.reports import TestReport

from shared.postgis import PostGISFixture
from shared.server import HonuaServer


# ---------------------------------------------------------------------------
# Paths and defaults
# ---------------------------------------------------------------------------

PROJECT_ROOT = Path(__file__).resolve().parents[3]
TESTS_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_SEED_PATH = TESTS_ROOT / "seed" / "client-compat-v1.sql"
DEFAULT_SERVICE_ID = "test_service"
DEFAULT_COLLECTION_ID = "0"
DEFAULT_PORT = 5575
DEFAULT_TIMEOUT_SECONDS = 120


def _get_worker_id() -> str:
    return os.getenv("PYTEST_XDIST_WORKER", "gw0")


def _get_worker_index(worker_id: str) -> int:
    if worker_id.startswith("gw") and worker_id[2:].isdigit():
        return int(worker_id[2:])
    return 0


# ---------------------------------------------------------------------------
# Seed-derived expectations
# ---------------------------------------------------------------------------

EXPECTED_TOTAL_FEATURES = 10
EXPECTED_GEOMETRY_FEATURES = 9
EXPECTED_ACTIVE_COUNT = 5
EXPECTED_ALPHA_X = -122.4900
EXPECTED_ALPHA_Y = 37.7100
EXPECTED_CRS_EPSG = 4326
GEO_TOLERANCE = 1e-6

EXPECTED_FIELD_NAMES = {
    "objectid", "name", "description", "status", "count", "ratio",
    "active", "created_at", "event_date", "event_time", "uid", "tags", "numbers",
}


# ---------------------------------------------------------------------------
# PyQGIS runtime dataclass
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class PyQgisCompatibilityRuntime:
    """Runtime metadata for the PyQGIS compatibility lane."""

    base_url: str
    mode: str  # "external" | "local"
    service_id: str
    collection_id: str
    seed_snapshot_name: str
    seed_snapshot_path: str
    server_version: str
    server_commit: str


# ---------------------------------------------------------------------------
# Certification evidence writer (envelope-based)
# ---------------------------------------------------------------------------

@dataclass
class _CertResult:
    test_case_id: str
    status: str
    duration_ms: int | None = None
    measured_count: int | None = None
    measured_delta: float | None = None
    notes: str = ""
    evidence_ref: str = ""


class CertificationEvidenceCollector:
    """Accumulates CERT-* results and writes a .cert.json envelope per protocol."""

    def __init__(
        self,
        runtime: PyQgisCompatibilityRuntime,
        client_version: str,
        protocol: str,
    ) -> None:
        self.runtime = runtime
        self.client_version = client_version
        self.protocol = protocol
        self._results: dict[str, _CertResult] = {}

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
        # Status precedence: fail > pass > skip = not-applicable. For an
        # equal status, prefer whichever record carries richer metadata so
        # post-run hooks (e.g. ``pytest_runtest_makereport``) cannot erase
        # explanatory data the test body already recorded. The PR #702
        # reviewer (discussion r3037619224) flagged that the previous
        # implementation overwrote richer skip evidence with bare longrepr
        # text from the report hook.
        new_record = _CertResult(
            test_case_id=test_case_id,
            status=status,
            duration_ms=duration_ms,
            measured_count=measured_count,
            measured_delta=measured_delta,
            notes=notes,
            evidence_ref=evidence_ref,
        )
        existing = self._results.get(test_case_id)
        if existing is None:
            self._results[test_case_id] = new_record
            return
        rank = {"fail": 3, "pass": 2, "skip": 1, "not-applicable": 1}
        new_rank = rank.get(status, 0)
        existing_rank = rank.get(existing.status, 0)
        if new_rank > existing_rank:
            self._results[test_case_id] = new_record
            return
        if new_rank < existing_rank:
            return

        def _richness(result: _CertResult) -> int:
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

        if _richness(new_record) > _richness(existing):
            self._results[test_case_id] = new_record

    def write_envelope(self, path: Path) -> None:
        results = list(self._results.values())
        summary = {
            "total": len(results),
            "passed": sum(1 for r in results if r.status == "pass"),
            "failed": sum(1 for r in results if r.status == "fail"),
            "skipped": sum(1 for r in results if r.status == "skip"),
            "not_applicable": sum(1 for r in results if r.status == "not-applicable"),
        }
        env = "ci" if os.getenv("CI") else "local"
        run_id = _utc_now_compact()
        envelope = {
            "schema_version": "1.0",
            "run_id": run_id,
            "run_date": _utc_now_iso(),
            "server_version": self.runtime.server_version,
            "client_lane": "desktop-qgis",
            "client_version": self.client_version,
            "protocol": self.protocol,
            "environment": env,
            "results": [
                {
                    "test_case_id": r.test_case_id,
                    "status": r.status,
                    "duration_ms": r.duration_ms,
                    "measured_count": r.measured_count,
                    "measured_delta": r.measured_delta,
                    "notes": r.notes,
                    "evidence_ref": r.evidence_ref,
                }
                for r in results
            ],
            "summary": summary,
            "cite_results": None,
            "extensions": [],
        }
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(envelope, indent=2))

    @property
    def has_records(self) -> bool:
        return len(self._results) > 0


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _utc_now_iso() -> str:
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def _utc_now_compact() -> str:
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")


def _read_server_version(base_url: str) -> str:
    configured = os.getenv("HONUA_PYQGIS_SERVER_VERSION")
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
    configured = os.getenv("HONUA_PYQGIS_SERVER_COMMIT")
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


def _get_qgis_version() -> str:
    """Return the QGIS version string from the runtime."""
    try:
        from qgis.core import Qgis
        return Qgis.version()
    except Exception:
        return "unknown"


def _discover_wfs_typename(base_url: str) -> str | None:
    """Parse GetCapabilities to find the first WFS type name."""
    caps_url = f"{base_url}/wfs?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetCapabilities"
    try:
        with urlopen(caps_url, timeout=30) as resp:  # noqa: S310
            tree = ET.parse(resp)
    except Exception:
        return None

    root = tree.getroot()
    ns = {
        "wfs": "http://www.opengis.net/wfs/2.0",
        "ows": "http://www.opengis.net/ows/1.1",
    }
    for ft in root.findall(".//wfs:FeatureType", ns):
        name_el = ft.find("wfs:Name", ns)
        if name_el is not None and name_el.text:
            return name_el.text.strip()
    # Fallback: no namespace
    for ft in root.iter():
        if ft.tag.endswith("FeatureType"):
            for child in ft:
                if child.tag.endswith("Name") and child.text:
                    return child.text.strip()
    return None


# ---------------------------------------------------------------------------
# Module-level collectors (singletons written at session teardown)
# ---------------------------------------------------------------------------

_oapif_evidence: CertificationEvidenceCollector | None = None
_wfs_evidence: CertificationEvidenceCollector | None = None


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------

@pytest.fixture(scope="session", autouse=True)
def _require_pyqgis():
    """Skip the entire pyqgis package when qgis.core is not importable."""
    try:
        import qgis.core  # noqa: F401
    except ImportError:
        pytest.skip(
            "PyQGIS not available — install QGIS to run "
            "PyQGIS desktop client compatibility tests."
        )


@pytest.fixture(scope="session")
def qgis_app():
    """Start a headless QgsApplication for the session."""
    os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

    from qgis.core import QgsApplication

    prefix = os.getenv("QGIS_PREFIX_PATH", "")
    app = QgsApplication([], False)
    if prefix:
        app.setPrefixPath(prefix, True)
    app.initQgis()
    yield app
    app.exitQgis()


@pytest.fixture(autouse=True)
def reset_worker_state() -> None:
    """Shadow the shared worker reset fixture with a no-op."""


@pytest.fixture(scope="session")
def pyqgis_runtime() -> Generator[PyQgisCompatibilityRuntime, None, None]:
    """Start a dedicated compatibility server or target an external URL."""
    base_url_override = os.getenv("HONUA_PYQGIS_BASE_URL")
    service_id = os.getenv("HONUA_PYQGIS_SERVICE_ID", DEFAULT_SERVICE_ID)
    collection_id = os.getenv("HONUA_PYQGIS_COLLECTION_ID", DEFAULT_COLLECTION_ID)
    seed_path = Path(os.getenv("HONUA_PYQGIS_SEED_PATH", str(DEFAULT_SEED_PATH)))
    seed_snapshot_name = seed_path.name

    def _build(base_url: str, mode: str) -> PyQgisCompatibilityRuntime:
        normalized = base_url.rstrip("/")
        commit = (
            _read_server_commit(PROJECT_ROOT)
            if mode == "local"
            else os.getenv("HONUA_PYQGIS_SERVER_COMMIT", "unknown")
        )
        return PyQgisCompatibilityRuntime(
            base_url=normalized,
            mode=mode,
            service_id=service_id,
            collection_id=collection_id,
            seed_snapshot_name=seed_snapshot_name,
            seed_snapshot_path=str(seed_path),
            server_version=_read_server_version(normalized),
            server_commit=commit,
        )

    if base_url_override:
        yield _build(base_url_override, "external")
        return

    fixture = PostGISFixture()
    fixture.start()
    fixture.apply_sql_file(seed_path)

    worker_id = _get_worker_id()
    port = int(os.getenv("HONUA_PYQGIS_PORT", str(DEFAULT_PORT))) + _get_worker_index(worker_id)
    server = HonuaServer(
        connection_string=fixture.get_npgsql_connection_string(),
        port=port,
        project_root=PROJECT_ROOT,
    )
    server.start(
        timeout=float(os.getenv("HONUA_PYQGIS_TIMEOUT", str(DEFAULT_TIMEOUT_SECONDS)))
    )

    try:
        yield _build(server.base_url, "local")
    finally:
        server.stop()
        fixture.stop()


@pytest.fixture(scope="session")
def base_url(pyqgis_runtime: PyQgisCompatibilityRuntime) -> str:
    """Expose the base URL for the compatibility server."""
    return pyqgis_runtime.base_url


@pytest.fixture(scope="session")
def test_service_id(pyqgis_runtime: PyQgisCompatibilityRuntime) -> str:
    return pyqgis_runtime.service_id


@pytest.fixture(scope="session")
def test_collection_id(pyqgis_runtime: PyQgisCompatibilityRuntime) -> str:
    return pyqgis_runtime.collection_id


@pytest.fixture(scope="session")
def qgis_version() -> str:
    return _get_qgis_version()


@pytest.fixture(scope="session")
def oapif_evidence(
    pyqgis_runtime: PyQgisCompatibilityRuntime,
    qgis_version: str,
) -> CertificationEvidenceCollector:
    """Session-scoped OGC API Features certification evidence collector."""
    global _oapif_evidence
    if _oapif_evidence is None:
        _oapif_evidence = CertificationEvidenceCollector(
            pyqgis_runtime, qgis_version, "ogc-features"
        )
    return _oapif_evidence


@pytest.fixture(scope="session")
def wfs_evidence(
    pyqgis_runtime: PyQgisCompatibilityRuntime,
    qgis_version: str,
) -> CertificationEvidenceCollector:
    """Session-scoped WFS certification evidence collector."""
    global _wfs_evidence
    if _wfs_evidence is None:
        _wfs_evidence = CertificationEvidenceCollector(
            pyqgis_runtime, qgis_version, "wfs"
        )
    return _wfs_evidence


@pytest.fixture(scope="session")
def wfs_typename(base_url: str) -> str:
    """Discover and cache the first WFS type name from GetCapabilities.

    Default behavior is to skip WFS-dependent tests when the endpoint cannot
    be reached or returns no FeatureType entries — this lets dev environments
    that have no WFS surface deployed still run the OAPIF subset of the
    suite.

    Set ``HONUA_PYQGIS_REQUIRE_WFS=1`` (used by nightly CI lanes that have a
    real WFS endpoint deployed) to convert that skip into a hard failure.
    The PR #702 reviewer (discussion r3037619223) correctly flagged that
    silently skipping a broken WFS endpoint hides regressions in
    certification runs, so any environment that promises WFS coverage must
    set this flag to fail loudly when WFS goes dark.
    """
    name = _discover_wfs_typename(base_url)
    if name is not None:
        return name
    caps_url = (
        f"{base_url}/wfs?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetCapabilities"
    )
    require_wfs = os.getenv("HONUA_PYQGIS_REQUIRE_WFS", "").strip().lower() in {
        "1",
        "true",
        "yes",
        "on",
    }
    base_message = (
        f"WFS GetCapabilities at {caps_url} returned no FeatureType entries "
        "(endpoint missing, unreachable, or empty)."
    )
    if require_wfs:
        pytest.fail(
            base_message
            + " HONUA_PYQGIS_REQUIRE_WFS is set, so this is treated as a "
            "hard failure rather than a skip."
        )
    pytest.skip(
        base_message
        + " Set HONUA_PYQGIS_REQUIRE_WFS=1 in environments that promise WFS "
        "coverage so this becomes a hard failure instead of a silent skip."
    )


# ---------------------------------------------------------------------------
# QGIS layer helpers
# ---------------------------------------------------------------------------

def make_oapif_layer(base_url: str, collection_id: str, *, extra_params: str = ""):
    """Construct a QGIS vector layer via the OGC API Features (OAPIF) provider."""
    from qgis.core import QgsVectorLayer

    # QGIS' OAPIF provider names the OGC API collection id parameter "typename".
    # Numeric collection IDs must remain unquoted; QGIS 3.44 treats quoted "0"
    # as an empty OAPIF typename and then requests /collections//items.
    typename = collection_id if collection_id.isdecimal() else f"'{collection_id}'"
    uri = f"url='{base_url}/ogc/features' typename={typename}"
    if extra_params:
        uri = f"{uri} {extra_params}"
    return QgsVectorLayer(uri, "oapif_test", "OAPIF")


def make_wfs_layer(base_url: str, typename: str, *, extra_params: str = ""):
    """Construct a QGIS vector layer via the WFS provider."""
    from qgis.core import QgsVectorLayer

    caps_url = f"{base_url}/wfs?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetCapabilities"
    uri = f"url='{caps_url}' typename='{typename}'"
    if extra_params:
        uri = f"{uri} {extra_params}"
    return QgsVectorLayer(uri, "wfs_test", "WFS")


def render_layer_headless(layer, width: int = 256, height: int = 256) -> bytes:
    """Render a QGIS vector layer to a PNG image and return raw bytes.

    Returns empty bytes if rendering fails. The caller asserts on the result.
    """
    from qgis.core import (
        QgsCoordinateReferenceSystem,
        QgsCoordinateTransform,
        QgsMapRendererSequentialJob,
        QgsMapSettings,
        QgsProject,
    )
    from qgis.PyQt.QtCore import QBuffer, QIODevice, QSize
    from qgis.PyQt.QtGui import QImage

    project = QgsProject.instance()
    project.addMapLayer(layer, False)

    # Visual / style slice geodesy lock — render in EPSG:3857 so the slice
    # scenarios match the JS lanes that use Web Mercator. The OAPIF provider
    # sources EPSG:4326 features and QGIS reprojects them on the fly.
    # QgsMapSettings.setExtent stores its argument as-is and the renderer
    # interprets the values in the destination CRS, so the layer extent must
    # be projected into EPSG:3857 before it is handed to setExtent — without
    # the transform, a 4326 lon/lat rectangle would collapse to a sub-meter
    # strip near the prime meridian and the features would render outside
    # the visible region.
    target_crs = QgsCoordinateReferenceSystem("EPSG:3857")
    extent_xform = QgsCoordinateTransform(layer.crs(), target_crs, project)
    extent_in_target = extent_xform.transformBoundingBox(layer.extent())

    settings = QgsMapSettings()
    settings.setDestinationCrs(target_crs)
    settings.setLayers([layer])
    settings.setOutputSize(QSize(width, height))
    settings.setExtent(extent_in_target)

    job = QgsMapRendererSequentialJob(settings)
    job.start()
    job.waitForFinished()

    image: QImage = job.renderedImage()
    project.removeMapLayer(layer.id())

    buf = QBuffer()
    buf.open(QIODevice.WriteOnly)
    image.save(buf, "PNG")
    return bytes(buf.data())


def render_layer_headless_with_symbol(
    layer,
    *,
    geometry_kind: str,
    fill_color: tuple[int, int, int],
    stroke_color: tuple[int, int, int],
    stroke_width: float = 1.0,
    marker_size: float = 6.0,
    width: int = 256,
    height: int = 256,
) -> bytes:
    """Render a QGIS vector layer with an explicit per-category symbol.

    Used by the visual / style certification slice (ticket #478) to
    substantiate `CERT-RNDR-{SYM,LIN,FIL}-01`. The slice declares known
    RGB constants per category; the lane test then asserts that color
    appears in the rendered PNG above a configured pixel-count
    threshold. This avoids the QGIS-LTR-version-pinned baseline
    diffing trap that the design brief flagged.

    Parameters
    ----------
    layer : QgsVectorLayer
        The vector layer to render. Must be valid before this call.
    geometry_kind : str
        One of ``"point"``, ``"line"``, ``"polygon"``. Selects which
        QgsSymbol subclass to construct. The current `client-compat-v1`
        seed only ships point geometries; the other branches are kept
        ready for the slice's polygon / line fixture follow-on.
    fill_color, stroke_color : tuple[int, int, int]
        RGB triples (0-255) for the symbol's fill and stroke. The
        rendered canvas is sampled for these by the lane test.
    stroke_width : float
        Stroke width in millimeters (QGIS unit default).
    marker_size : float
        Marker size in millimeters when ``geometry_kind == "point"``.
    """
    from qgis.core import (
        QgsFillSymbol,
        QgsLineSymbol,
        QgsMarkerSymbol,
        QgsSingleSymbolRenderer,
    )
    from qgis.PyQt.QtGui import QColor

    fill_qcolor = QColor(*fill_color)
    stroke_qcolor = QColor(*stroke_color)

    if geometry_kind == "point":
        symbol = QgsMarkerSymbol.createSimple(
            {
                "name": "circle",
                "color": fill_qcolor.name(),
                "outline_color": stroke_qcolor.name(),
                "outline_width": str(stroke_width),
                "size": str(marker_size),
            }
        )
    elif geometry_kind == "line":
        symbol = QgsLineSymbol.createSimple(
            {
                "color": stroke_qcolor.name(),
                "width": str(stroke_width),
            }
        )
    elif geometry_kind == "polygon":
        symbol = QgsFillSymbol.createSimple(
            {
                "color": fill_qcolor.name(),
                "outline_color": stroke_qcolor.name(),
                "outline_width": str(stroke_width),
            }
        )
    else:
        raise ValueError(
            f"Unknown geometry_kind={geometry_kind!r}; expected point/line/polygon."
        )

    layer.setRenderer(QgsSingleSymbolRenderer(symbol))
    layer.triggerRepaint()

    # Delegate to render_layer_headless so the EPSG:3857 reproject + extent
    # transform live in exactly one place. The slice tests only differ from
    # the base render path by the renderer assignment above.
    return render_layer_headless(layer, width=width, height=height)


# ---------------------------------------------------------------------------
# Evidence teardown — write .cert.json envelopes at session end
# ---------------------------------------------------------------------------

@pytest.fixture(scope="session", autouse=True)
def _write_cert_evidence(
    oapif_evidence: CertificationEvidenceCollector,
    wfs_evidence: CertificationEvidenceCollector,
) -> Generator[None, None, None]:
    """Persist .cert.json envelopes at session teardown."""
    yield
    results_dir = TESTS_ROOT / "TestResults"
    run_id = _utc_now_compact()
    worker_id = os.environ.get("PYTEST_XDIST_WORKER")
    suffix = "" if not worker_id or worker_id == "master" else f"-{worker_id}"

    if oapif_evidence.has_records:
        path = results_dir / f"{run_id}-desktop-qgis-ogc-features{suffix}.cert.json"
        oapif_evidence.write_envelope(path)

    if wfs_evidence.has_records:
        path = results_dir / f"{run_id}-desktop-qgis-wfs{suffix}.cert.json"
        wfs_evidence.write_envelope(path)


# ---------------------------------------------------------------------------
# Hook: record failures and skips so evidence survives regressions
# ---------------------------------------------------------------------------

@pytest.hookimpl(hookwrapper=True)
def pytest_runtest_makereport(item: pytest.Item, call):
    """Automatically record test failures and skips in evidence."""
    outcome = yield
    report: TestReport = outcome.get_result()

    collector = _collector_for_item(item)
    if collector is None:
        return

    cert_id = _extract_cert_id(item)
    if cert_id is None:
        return

    if report.when == "call" and report.failed:
        collector.record(
            cert_id, "fail",
            notes=(report.longreprtext or "")[:500],
        )
    elif report.skipped:
        collector.record(
            cert_id, "skip",
            notes=str(report.longrepr)[:500] if report.longrepr else "",
        )


def _collector_for_item(item: pytest.Item) -> CertificationEvidenceCollector | None:
    """Determine which evidence collector an item belongs to."""
    module = Path(item.fspath).stem
    if "oapif" in module or "render" in module:
        return _oapif_evidence
    if "wfs" in module:
        return _wfs_evidence
    return None


def _extract_cert_id(item: pytest.Item) -> str | None:
    """Extract a CERT-* ID from the closest ``cert`` marker, if present."""
    marker = item.get_closest_marker("cert")
    if marker and marker.args:
        return marker.args[0]
    return None
