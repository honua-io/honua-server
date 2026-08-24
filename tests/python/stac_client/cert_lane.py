# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Registered certification-lane wiring for the ``py-pystac`` STAC lane.

This module is the lane's *declaration*: which common-core CERT-* cases the
pystac-client lane substantiates, which are structurally not-applicable, how
the ``.cert.json`` envelope is addressed, and the handful of transport-level
helpers the cases need where the client library exposes no observable surface.

Shape mirrors the other canonical-client lanes
(``tests/python/pyqgis/conftest.py`` for the fixture/teardown shape, and
``tests/python/shared/cert_envelope.py`` for the envelope schema itself, which
is defined by ``docs/gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md``).

Two deliberate choices:

``client`` vs ``transport``
    Every data-plane case is driven through the real ``pystac`` /
    ``pystac_client`` API (``Client.open``, ``get_collections``,
    ``get_collection``, ``search``, ``ItemSearch.pages``), because the point of
    certifying a client is to prove the *server* is compatible with what that
    client actually does. Raw ``httpx`` appears only where pystac-client gives
    no observable surface: the ``CERT-AUTH-*`` control-plane probe (not a STAC
    endpoint at all), the transport-shape assertions, and the error-surface
    extension cases where the useful evidence is the RFC 7807 body and the HTTP
    status rather than the exception text. Every such case says so in its
    ``notes``.

Applicability
    All eight ``CERT-RNDR-*`` facets are structurally not-applicable:
    pystac-client is a catalog/metadata client with no drawing surface. The
    shared collector emits them automatically, so they are never recorded here.
"""

from __future__ import annotations

import importlib.metadata
import os
import re
from collections.abc import Iterable, Mapping
from pathlib import Path
from typing import Any
from urllib.parse import urlsplit

import httpx

from shared import canonical_fixture, cert_envelope
from shared.cert_envelope import CertificationEvidenceCollector


# ---------------------------------------------------------------------------
# Lane identity
# ---------------------------------------------------------------------------

#: Compose service name / lane output directory (``docker/client-compat/pystac``).
COMPOSE_SERVICE = "pystac"

#: ``client_lane`` recorded in the evidence envelope.
CLIENT_LANE = "py-pystac"

#: ``protocol`` recorded in the evidence envelope.
PROTOCOL = "stac"

#: Envelope filename shape. ``scripts/client-compat/refresh-baselines.sh``
#: strips everything up to the first ``-`` to derive the stable baseline name,
#: so the run id must not contain one (``cert_envelope.utc_now_compact()``).
ENVELOPE_SUFFIX = f"{CLIENT_LANE}-{PROTOCOL}.cert.json"


# ---------------------------------------------------------------------------
# Environment contract
# ---------------------------------------------------------------------------

#: Base URL the existing suite already honors; the container lane sets it.
BASE_URL_ENV = "HONUA_STAC_COMPAT_BASE_URL"

#: Shared fallback used by the other docker/client-compat lanes.
FALLBACK_BASE_URL_ENV = "HONUA_BASE_URL"

#: Where the ``.cert.json`` envelope is written (``/output`` in the container).
OUTPUT_DIR_ENV = "HONUA_PYSTAC_OUTPUT_DIR"

SERVER_VERSION_ENV = "HONUA_PYSTAC_SERVER_VERSION"
SERVER_COMMIT_ENV = "HONUA_PYSTAC_SERVER_COMMIT"


def external_base_url() -> str | None:
    """Return the externally supplied base URL, if any.

    ``HONUA_STAC_COMPAT_BASE_URL`` wins so the existing lane contract is
    unchanged; ``HONUA_BASE_URL`` is the shared docker/client-compat variable
    every other lane already honors.
    """
    for name in (BASE_URL_ENV, FALLBACK_BASE_URL_ENV):
        value = os.getenv(name)
        if value and value.strip():
            return value.strip()
    return None


# ---------------------------------------------------------------------------
# Applicability contract
# ---------------------------------------------------------------------------

#: The 16 common-core IDs this lane substantiates against the STAC surface.
APPLICABLE_CASES: frozenset[str] = frozenset({
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
    "pystac-client is a catalog/metadata client with no drawing surface; "
    "rendering facets are structurally not applicable."
)


# ---------------------------------------------------------------------------
# STAC conformance vocabulary
# ---------------------------------------------------------------------------

#: STAC API conformance-class suffixes, matched the way pystac-client matches
#: them (``https://api.stacspec.org/v1.0.*<suffix>``) so a server that advances
#: its patch version does not read as a missing class.
STAC_API_PREFIX = "https://api.stacspec.org/v"

CONFORMANCE_CORE = "/core"
CONFORMANCE_COLLECTIONS = "/collections"
CONFORMANCE_FEATURES = "/ogcapi-features"
CONFORMANCE_ITEM_SEARCH = "/item-search"
CONFORMANCE_FIELDS = "/item-search#fields"
CONFORMANCE_SORT = "/item-search#sort"
CONFORMANCE_FILTER = "/item-search#filter"

#: Absolute (non-STAC-API) conformance URIs the landing page also declares.
OGC_FEATURES_CORE = "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core"
OGC_FEATURES_OAS30 = "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/oas30"
OGC_FEATURES_GEOJSON = "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson"
OGC_FEATURES_FILTER = "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/filter"
CQL2_BASIC = "http://www.opengis.net/spec/cql2/1.0/conf/basic-cql2"
CQL2_TEXT = "http://www.opengis.net/spec/cql2/1.0/conf/cql2-text"
CQL2_JSON = "http://www.opengis.net/spec/cql2/1.0/conf/cql2-json"

_STAC_API_VERSION_PATTERN = re.compile(
    r"^https://api\.stacspec\.org/v(?P<version>[0-9][0-9A-Za-z.\-]*)/core$"
)


def declared_conformance(landing: Mapping[str, Any]) -> tuple[str, ...]:
    """Return the ``conformsTo`` URIs the landing page advertises."""
    raw = landing.get("conformsTo")
    if not isinstance(raw, list):
        return ()
    return tuple(str(entry).strip() for entry in raw if str(entry).strip())


def declares_stac_api_class(landing: Mapping[str, Any], suffix: str) -> bool:
    """Whether a STAC API conformance class (``/core``, ``#filter``, ...) is declared."""
    for uri in declared_conformance(landing):
        if uri.startswith(STAC_API_PREFIX) and uri.endswith(suffix):
            return True
    return False


def declares_uri(landing: Mapping[str, Any], uri: str) -> bool:
    """Whether an absolute conformance URI is declared verbatim."""
    return uri in declared_conformance(landing)


def missing_stac_api_classes(
    landing: Mapping[str, Any],
    suffixes: Iterable[str],
) -> tuple[str, ...]:
    """Return the requested STAC API conformance suffixes the server does not declare.

    A facet that cannot be substantiated because its conformance class is
    absent must be recorded ``fail``/``skip`` naming the class — never silently
    downgraded to ``not-applicable``, which would read as "this client cannot
    do that" rather than "this server does not implement that".
    """
    return tuple(
        suffix for suffix in suffixes if not declares_stac_api_class(landing, suffix)
    )


def detect_protocol_version(landing: Mapping[str, Any]) -> str:
    """Read the STAC API version the server advertises on its landing page.

    Preference order: the version embedded in the ``.../core`` conformance URI
    (that is the *API* version), then ``stac_version`` (the object-model
    version), then ``unknown``. Never hardcoded.
    """
    for uri in declared_conformance(landing):
        match = _STAC_API_VERSION_PATTERN.match(uri)
        if match:
            return match.group("version")

    stac_version = landing.get("stac_version")
    if isinstance(stac_version, str) and stac_version.strip():
        return stac_version.strip()

    return "unknown"


# ---------------------------------------------------------------------------
# Transport helpers (see the module docstring for why these exist)
# ---------------------------------------------------------------------------

DEFAULT_TIMEOUT_SECONDS = 30.0


def stac_root_url(base_url: str) -> str:
    """Return the STAC landing-page URL for a server base URL."""
    return f"{base_url.rstrip('/')}/stac"


def read_landing_page(base_url: str) -> dict[str, Any]:
    """Fetch the raw STAC landing page.

    Used for lane wiring (protocol-version detection and the conformance-class
    gap checks) rather than as a certification assertion: pystac-client does not
    expose the raw ``conformsTo`` document in a form that survives rehydration.
    """
    response = httpx.get(
        stac_root_url(base_url),
        timeout=DEFAULT_TIMEOUT_SECONDS,
        headers={"Accept": "application/json"},
    )
    response.raise_for_status()
    payload = response.json()
    if not isinstance(payload, dict):
        raise TypeError(f"STAC landing page returned {type(payload).__name__}, expected object")
    return payload


def get_json(url: str, **kwargs: Any) -> httpx.Response:
    """Issue a raw GET, returning the response without raising for status."""
    kwargs.setdefault("timeout", DEFAULT_TIMEOUT_SECONDS)
    headers = dict(kwargs.pop("headers", {}) or {})
    headers.setdefault("Accept", "application/json")
    return httpx.get(url, headers=headers, **kwargs)


def post_json(url: str, payload: Mapping[str, Any], **kwargs: Any) -> httpx.Response:
    """Issue a raw POST, returning the response without raising for status."""
    kwargs.setdefault("timeout", DEFAULT_TIMEOUT_SECONDS)
    headers = dict(kwargs.pop("headers", {}) or {})
    headers.setdefault("Accept", "application/geo+json")
    return httpx.post(url, json=dict(payload), headers=headers, **kwargs)


def problem_summary(response: httpx.Response) -> str:
    """Summarize an RFC 7807 problem body for an evidence note."""
    try:
        body = response.json()
    except ValueError:
        return f"status={response.status_code} body={response.text[:120]!r}"
    if isinstance(body, dict):
        return (
            f"status={response.status_code} "
            f"title={body.get('title')!r} detail={body.get('detail')!r}"
        )
    return f"status={response.status_code} body={str(body)[:120]!r}"


def admin_probe_url(base_url: str) -> str:
    """Return the control-plane URL used to substantiate ``CERT-AUTH-*``."""
    return f"{base_url.rstrip('/')}{canonical_fixture.ADMIN_PROBE_PATH}"


def admin_auth_headers() -> dict[str, str]:
    """Return the header pair that authenticates against the Honua control plane.

    Honua's admin surface is API-key authenticated
    (``src/Honua.Hosting/Features/Authentication/ApiKeyAuthenticationHandler.cs``):
    the bootstrap key is ``HONUA_ADMIN_PASSWORD`` presented in ``X-API-Key``.
    HTTP Basic is a *compatibility* mode that is off by default and additionally
    refuses non-HTTPS transport, so it cannot work on the plain-HTTP compose
    network; there is no bearer/login flow for this surface.
    """
    return {canonical_fixture.ADMIN_API_KEY_HEADER: canonical_fixture.ADMIN_API_KEY}


# ---------------------------------------------------------------------------
# Collector construction and envelope output
# ---------------------------------------------------------------------------

def client_version() -> str:
    """Return the ``pystac=<ver>;pystac-client=<ver>`` version string."""
    return f"pystac={_package_version('pystac')};pystac-client={_package_version('pystac-client')}"


def _package_version(name: str) -> str:
    try:
        return importlib.metadata.version(name)
    except importlib.metadata.PackageNotFoundError:
        return "unknown"


def build_collector(
    base_url: str,
    landing: Mapping[str, Any] | None = None,
) -> CertificationEvidenceCollector:
    """Build the lane's evidence collector with its receipt bindings."""
    resolved_landing = landing if landing is not None else read_landing_page(base_url)
    runtime = cert_envelope.build_lane_runtime(
        base_url=base_url,
        project_root=canonical_fixture.PROJECT_ROOT,
        fixture_path=canonical_fixture.SEED_PATH,
        server_config_path=canonical_fixture.SERVER_CONFIG_PATH,
        version_env=SERVER_VERSION_ENV,
        commit_env=SERVER_COMMIT_ENV,
    )
    return CertificationEvidenceCollector(
        runtime,
        client_lane=CLIENT_LANE,
        client_version=client_version(),
        protocol=PROTOCOL,
        protocol_version=detect_protocol_version(resolved_landing),
        applicable=APPLICABLE_CASES,
        not_applicable_reason=NOT_APPLICABLE_REASON,
    )


def envelope_output_dir() -> Path:
    """Return the directory the ``.cert.json`` envelope is written to."""
    override = os.environ.get(OUTPUT_DIR_ENV)
    if override and override.strip():
        return Path(override.strip())
    return canonical_fixture.TESTS_ROOT / "TestResults"


def envelope_path(output_dir: Path | None = None, run_id: str | None = None) -> Path:
    """Return the full ``{run_id}-py-pystac-stac.cert.json`` path."""
    directory = output_dir if output_dir is not None else envelope_output_dir()
    resolved_run_id = run_id or cert_envelope.utc_now_compact()
    return directory / f"{resolved_run_id}-{ENVELOPE_SUFFIX}"


def write_envelope(collector: CertificationEvidenceCollector) -> Path:
    """Persist the lane's evidence envelope and return where it landed."""
    path = envelope_path()
    collector.write_envelope(path)
    return path


def transport_scheme(base_url: str) -> str:
    """Return the URL scheme of the target server."""
    return (urlsplit(base_url).scheme or "").lower()
