# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Fixtures and certification-evidence wiring for the DuckDB Spatial lane.

DuckDB Spatial is the analytical-SQL canonical client in the interop matrix
([#3392](https://github.com/honua-io/honua-server/issues/3392)). Everything the
lane observes is driven through real DuckDB SQL — ``ST_Read`` over the GDAL
``OAPIF`` driver for feature access, ``read_json_auto`` over ``httpfs`` for the
OGC API Features metadata documents, and ordinary DuckDB SQL (aggregates,
joins, window functions, ``COPY ... TO``) on top of the result sets.

``httpx`` is used only where DuckDB exposes no observable surface: the
control-plane ``CERT-AUTH-*`` probe (DuckDB cannot report an HTTP status code
or a ``WWW-Authenticate`` challenge) and transport-shape assertions such as the
``Content-Crs`` response header and the ``400`` vs ``404`` distinction behind a
``duckdb.IOException``. Every case that leans on ``httpx`` says so in its
``notes``.

The envelope schema, receipt bindings and status precedence come from
``tests/python/shared/cert_envelope.py``; the fixture expectations come from
``tests/python/shared/canonical_fixture.py``. Neither is duplicated here.
"""

from __future__ import annotations

import os
from pathlib import Path
from typing import Generator

import duckdb
import pytest
from _pytest.reports import TestReport

from shared import canonical_fixture
from shared.cert_envelope import (
    CertificationEvidenceCollector,
    build_lane_runtime,
    utc_now_compact,
)

# ---------------------------------------------------------------------------
# Lane identity
# ---------------------------------------------------------------------------

CLIENT_LANE = "duckdb"
PROTOCOL = "ogc-features"
PROTOCOL_VERSION = "1.0"

#: Common-core IDs this lane is contractually required to substantiate. The
#: eight ``CERT-RNDR-*`` facets are deliberately absent: DuckDB Spatial has no
#: drawing surface at all, so the collector emits them as ``not-applicable``.
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
    "DuckDB Spatial is an analytical SQL client with no drawing surface; "
    "rendering facets are structurally not applicable."
)

TESTS_ROOT = Path(__file__).resolve().parents[2]


def pytest_configure(config: pytest.Config) -> None:
    """Register the lane's markers.

    ``tests/python/pytest.ini`` already declares it, but the lane is also run
    with ``--confcutdir`` from the container where only this package is
    collected; registering here keeps the marker known in every invocation.
    """
    config.addinivalue_line(
        "markers",
        "cert(*case_ids): CERT-*/NB-DDB-* certification case identifiers.",
    )
    config.addinivalue_line(
        "markers",
        "duckdb_client: DuckDB Spatial canonical analyst client lane (#3392).",
    )


# ---------------------------------------------------------------------------
# Base URL
# ---------------------------------------------------------------------------

def _resolve_base_url() -> str:
    """Resolve the target server URL; fail loudly when it is not configured.

    There is deliberately no local-server fallback. The lane certifies a
    deployed candidate, and silently spinning up a private server would make a
    green envelope mean nothing about the artifact under test.
    """
    raw = os.getenv("HONUA_DUCKDB_BASE_URL") or os.getenv("HONUA_BASE_URL")
    if not raw:
        raise RuntimeError(
            "The DuckDB Spatial certification lane needs a target server. Set "
            "HONUA_DUCKDB_BASE_URL (or HONUA_BASE_URL) to the base URL of the "
            "server under certification, e.g. http://honua:5000."
        )
    return raw.rstrip("/")


# ---------------------------------------------------------------------------
# Module-level collector (written once at session teardown)
# ---------------------------------------------------------------------------

_evidence: CertificationEvidenceCollector | None = None


def _client_version(connection: duckdb.DuckDBPyConnection) -> str:
    """Return ``duckdb=<ver>;spatial=<ver>`` for the running client."""
    spatial = "unknown"
    try:
        row = connection.execute(
            "SELECT extension_version FROM duckdb_extensions() "
            "WHERE extension_name = 'spatial'"
        ).fetchone()
        if row and row[0]:
            spatial = str(row[0])
    except duckdb.Error:  # pragma: no cover - defensive
        pass
    return f"duckdb={duckdb.__version__};spatial={spatial}"


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------

@pytest.fixture(autouse=True)
def reset_worker_state() -> None:
    """Shadow the shared worker-reset fixture with a no-op.

    ``tests/python/conftest.py`` declares an autouse ``reset_worker_state``
    that depends on a live PostGIS Testcontainer. This lane certifies an
    already-running deployment and must never start a database of its own, so
    the fixture is shadowed here exactly as the pyqgis and stac_client lanes
    do.
    """


@pytest.fixture(scope="session")
def base_url() -> str:
    """Base URL of the server under certification."""
    return _resolve_base_url()


@pytest.fixture(scope="session")
def oapif_root(base_url: str) -> str:
    """OGC API Features landing-page URL (no ``OAPIF:`` driver prefix)."""
    return f"{base_url}/ogc/features"


@pytest.fixture(scope="session")
def oapif_dsn(oapif_root: str) -> str:
    """GDAL ``OAPIF:`` dataset string for the landing page.

    Opening the landing page (rather than a single collection) makes every
    published collection a GDAL layer, which is what a DuckDB analyst gets
    when they point ``ST_Read`` at a Honua deployment.
    """
    return f"OAPIF:{oapif_root}"


@pytest.fixture(scope="session")
def duckdb_connection() -> Generator[duckdb.DuckDBPyConnection, None, None]:
    """A session-scoped DuckDB connection with spatial + httpfs loaded.

    The extensions are baked into the image at build time (see
    ``docker/client-compat/duckdb/Dockerfile``), so ``INSTALL`` is a no-op
    offline and ``LOAD`` resolves from ``DUCKDB_EXTENSION_DIRECTORY``. Keeping
    ``INSTALL`` in the statement means the lane still works on a developer
    machine that has network access and no pre-seeded extension directory.
    """
    connection = duckdb.connect()
    connection.execute("INSTALL spatial; LOAD spatial; INSTALL httpfs; LOAD httpfs;")
    try:
        yield connection
    finally:
        connection.close()


@pytest.fixture(scope="session")
def duckdb_client_version(duckdb_connection: duckdb.DuckDBPyConnection) -> str:
    """``duckdb=<ver>;spatial=<ver>`` recorded in the envelope."""
    return _client_version(duckdb_connection)


@pytest.fixture(scope="session")
def evidence(
    base_url: str,
    duckdb_client_version: str,
) -> CertificationEvidenceCollector:
    """Session-scoped certification-evidence collector for this lane."""
    global _evidence
    if _evidence is None:
        runtime = build_lane_runtime(
            base_url=base_url,
            project_root=canonical_fixture.PROJECT_ROOT,
            fixture_path=canonical_fixture.SEED_PATH,
            server_config_path=canonical_fixture.SERVER_CONFIG_PATH,
            version_env="HONUA_DUCKDB_SERVER_VERSION",
            commit_env="HONUA_DUCKDB_SERVER_COMMIT",
        )
        _evidence = CertificationEvidenceCollector(
            runtime,
            client_lane=CLIENT_LANE,
            client_version=duckdb_client_version,
            protocol=PROTOCOL,
            protocol_version=PROTOCOL_VERSION,
            applicable=APPLICABLE_CASES,
            not_applicable_reason=NOT_APPLICABLE_REASON,
        )
    return _evidence


@pytest.fixture(scope="session", autouse=True)
def _write_cert_evidence(
    evidence: CertificationEvidenceCollector,
) -> Generator[None, None, None]:
    """Persist the ``.cert.json`` envelope at session teardown.

    ``HONUA_DUCKDB_OUTPUT_DIR`` overrides the destination; the compose lane
    points it at ``/output`` because ``tests/`` is bind-mounted read-only.
    ``run_id`` uses ``utc_now_compact()``, which contains no ``-`` so that
    ``scripts/client-compat/refresh-baselines.sh`` can strip up to the first
    ``-`` to recover the lane name.
    """
    yield
    override = os.environ.get("HONUA_DUCKDB_OUTPUT_DIR")
    results_dir = Path(override) if override else TESTS_ROOT / "TestResults"
    results_dir.mkdir(parents=True, exist_ok=True)
    run_id = utc_now_compact()
    evidence.write_envelope(
        results_dir / f"{run_id}-{CLIENT_LANE}-{PROTOCOL}.cert.json"
    )


# ---------------------------------------------------------------------------
# Hook: unexpected failures and skips still land in the envelope
# ---------------------------------------------------------------------------

@pytest.hookimpl(hookwrapper=True)
def pytest_runtest_makereport(item: pytest.Item, call):
    """Record failures/skips for ``@pytest.mark.cert``-marked tests.

    A test body that fails before its own ``record`` call would otherwise leave
    the case as an unexplained ``skip``; this hook downgrades it to ``fail``
    with the failure text so the baseline diff sees the regression.
    """
    outcome = yield
    report: TestReport = outcome.get_result()

    if _evidence is None:
        return

    marker = item.get_closest_marker("cert")
    if marker is None or not marker.args:
        return

    # A test may substantiate one common-core ID plus one or more lane
    # extension IDs; every ID it claims must reflect the outcome.
    for case_id in marker.args:
        # try_record, not record: the hook fires for every test, and a strict
        # record() would raise (and take the session down) if a marker ever
        # named a case this lane declares not-applicable.
        if report.when == "call" and report.failed:
            _evidence.try_record(case_id, "fail", notes=(report.longreprtext or "")[:500])
        elif report.skipped:
            _evidence.try_record(
                case_id,
                "skip",
                notes=str(report.longrepr)[:500] if report.longrepr else "",
            )
