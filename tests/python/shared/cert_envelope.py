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

Release-tier receipts
---------------------

The nightly ``.cert.json`` above answers this repository's own baseline diff.
The 2026.1 release gate answers a different consumer: the
``client-interop-cert-v1`` normalizer in
``honua-io/honua-evidence/scripts/fetch-certification-producers.py``, which
admits a receipt only when it independently binds the exact candidate
(``server_commit``, ``image_digest``), the trusted producer run
(``producer_source_sha``), all three governed revisions, the governed client
identity (``client_id``, ``runner_lane``, ``protocol_profile``) and, per
result, the request provenance (``performed_by``, ``request_url``,
``exercised_capabilities``). It also uses the underscored ``not_applicable``
token, not this repository's hyphenated one, and rejects the whole receipt on
any divergence.

``build_release_receipt`` produces that receipt. It is a strict superset of the
nightly envelope plus a vocabulary translation, and it fails closed at
emission: a run with no candidate image digest, no trusted producer SHA, or a
result that cannot name the request it performed cannot write one. That is
deliberate -- a source-built server must not be able to manufacture a
release-tier receipt. The contract it satisfies is mirrored in
``certification/client-protocol-requirements.v1.json`` under ``receiptContract``
and enforced by ``scripts/certification/verify-client-certification-receipts.py``.
"""

from __future__ import annotations

import datetime
import hashlib
import json
import os
import re
import subprocess
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import parse_qsl, urlparse

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
_RELEASE_STATUS_RANK = {"fail": 4, "skip": 3, "pass": 2, "not-applicable": 1}

# The governed status vocabulary spells the fourth token with an underscore. The
# lanes, the baseline diff and the matrix documentation all use the hyphenated
# form, so the translation happens once, here, on the way into a release receipt.
GOVERNED_STATUS = {
    "pass": "pass", "fail": "fail", "skip": "skip", "not-applicable": "not_applicable",
}

SHA_PATTERN = re.compile(r"^[0-9a-f]{40}$")
DIGEST_PATTERN = re.compile(r"^sha256:[0-9a-f]{64}$")

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
    client_identity: str = ""
    protocol_version: str | None = None
    #: Absolute URL the client actually requested. ``None`` only for a skip, where
    #: no request was performed. The governed consumer will not accept a claim
    #: about an endpoint the receipt cannot name.
    request_url: str | None = None
    #: Governed scenario facets this observation genuinely exercised. A pass may
    #: never claim a facet absent from this list.
    exercised_capabilities: tuple[str, ...] = ()


@dataclass(frozen=True)
class LaneRuntime:
    """Receipt bindings every envelope this lane emits must carry.

    The first six are the nightly bindings. The rest are the additional
    release-tier bindings; they stay ``None`` on a developer or nightly run and
    are required before ``build_release_receipt`` will emit anything.

    ``deployment_target`` is the governed execution context (``local-docker`` for
    every bounded-roster row today). It is part of the governed cell identity, so
    the receipt names it rather than letting a verifier infer it from
    ``environment`` -- evidence from one execution context must not certify a cell
    governed for another.
    """

    base_url: str
    environment: str
    server_version: str
    server_commit: str
    fixture_revision: str
    server_config_revision: str
    image_digest: str | None = None
    producer_source_sha: str | None = None
    auth_policy_revision: str | None = None
    deployment_target: str | None = None


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
        client_id: str | None = None,
        protocol_profile: str | None = None,
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
        # Release-tier identity. ``client_id`` is the governed canonical client name
        # (for example "OWSLib"), which the denominator matches on and which is not
        # interchangeable with the CI runner lane. ``protocol_profile`` names the
        # exercised wire contract. Both are optional at nightly tier and required
        # before a release receipt can be written.
        self.client_id = client_id
        self.protocol_profile = protocol_profile
        self._results: dict[str, CertResult] = {}
        self._extensions: dict[str, CertResult] = {}
        # Nightly envelopes retain their historical best-available skip/pass
        # behavior. Release qualification must retain every observed non-pass.
        self._release_results: dict[str, CertResult] = {}

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
        client_identity: str = "",
        protocol_version: str | None = None,
        request_url: str | None = None,
        exercised_capabilities: tuple[str, ...] | list[str] = (),
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
            client_identity=client_identity,
            protocol_version=protocol_version,
            request_url=request_url,
            exercised_capabilities=tuple(exercised_capabilities),
        )
        bucket = self._results if test_case_id in COMMON_CORE_IDS else self._extensions
        existing = bucket.get(test_case_id)
        if existing is None or _prefer(candidate, existing):
            bucket[test_case_id] = candidate
        existing_release = self._release_results.get(test_case_id)
        if existing_release is None or _prefer(candidate, existing_release, release=True):
            self._release_results[test_case_id] = candidate

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
            "summary": _summarize(statuses),
            "cite_results": None,
            "extensions": [_as_dict(entry) for entry in self._extensions.values()],
        }

    def write_envelope(self, path: Path) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(self.build_envelope(), indent=2) + "\n")

    # -- release tier ------------------------------------------------------

    def build_release_receipt(self) -> dict:
        """Project this lane's observations into a governed release-tier receipt.

        Fails closed rather than degrading: a run that cannot bind the exact
        candidate image, the trusted producer revision or the governed client
        identity raises instead of writing a receipt a release gate might admit.

        An individual passing observation that cannot name the request it performed, or
        the governed facets it exercised, is *omitted* rather than published with
        invented provenance. The governed aggregator emits a requirement it sees
        no observation for as a skip, which the release gate fails closed on, so
        omission costs nothing and publishing a malformed row would cost the whole
        receipt -- the consumer rejects an entire receipt on one bad result.
        An observed fail or skip without provenance instead rejects emission:
        omitting it could let another test credit the same governed operation.
        """
        missing = [
            name for name, value in (
                ("image_digest", self.runtime.image_digest),
                ("producer_source_sha", self.runtime.producer_source_sha),
                ("auth_policy_revision", self.runtime.auth_policy_revision),
                ("deployment_target", self.runtime.deployment_target),
                ("client_id", self.client_id),
                ("protocol_profile", self.protocol_profile),
            ) if not value
        ]
        if missing:
            raise ValueError(
                f"{self.client_lane}/{self.protocol} cannot emit a release receipt without "
                f"{sorted(missing)}; a release receipt must bind the exact candidate."
            )
        if not SHA_PATTERN.fullmatch(self.runtime.server_commit):
            raise ValueError(
                f"{self.client_lane}/{self.protocol} server_commit "
                f"{self.runtime.server_commit!r} is not an exact 40-character commit; a "
                "source-built server cannot produce a release receipt."
            )
        if not SHA_PATTERN.fullmatch(self.runtime.producer_source_sha or ""):
            raise ValueError("producer_source_sha must be an exact 40-character commit")
        if not DIGEST_PATTERN.fullmatch(self.runtime.image_digest or ""):
            raise ValueError(
                f"image_digest {self.runtime.image_digest!r} is not a sha256 registry digest; "
                "a locally built image cannot produce a release receipt."
            )

        envelope = self.build_envelope()
        substantiated: list[dict] = []
        unsubstantiated: list[dict] = []
        for entry in [*envelope["results"], *envelope["extensions"]]:
            recorded = self._release_results.get(entry["test_case_id"])
            governed = _as_dict(recorded) if recorded is not None else dict(entry)
            governed["status"] = GOVERNED_STATUS[governed["status"]]
            if governed["status"] == "not_applicable":
                # The consumer discards these before it checks provenance.
                substantiated.append(governed)
                continue

            provenance = _release_provenance(recorded, self.client_id or "")
            if provenance is None:
                if recorded is not None and recorded.status in {"fail", "skip"}:
                    raise ValueError(
                        f"Cannot omit nonpassing observation {recorded.test_case_id}: "
                        "release request provenance is missing or invalid.")
                unsubstantiated.append({
                    "test_case_id": entry["test_case_id"],
                    "status": governed["status"],
                    "reason": (
                        "The observation did not record an absolute request_url and the "
                        "governed facets it exercised, so it cannot be published as evidence."),
                })
                continue
            governed.update(provenance)
            substantiated.append(governed)

        if not any(entry["status"] != "not_applicable" for entry in substantiated):
            raise ValueError(
                f"{self.client_lane}/{self.protocol} substantiated no executable observation; "
                f"{len(unsubstantiated)} were omitted for missing request provenance."
            )

        results = [
            entry for entry in substantiated if entry["test_case_id"] in COMMON_CORE_IDS]
        return {
            **{key: envelope[key] for key in (
                "schema_version", "run_id", "run_date", "server_version", "server_commit",
                "fixture_revision", "server_config_revision", "client_lane", "client_version",
                "protocol", "protocol_version", "environment", "cite_results")},
            "producer_source_sha": self.runtime.producer_source_sha,
            "image_digest": self.runtime.image_digest,
            "auth_policy_revision": self.runtime.auth_policy_revision,
            "deployment_target": self.runtime.deployment_target,
            "client_id": self.client_id,
            "runner_lane": self.client_lane,
            "protocol_profile": self.protocol_profile,
            "results": results,
            "extensions": [
                entry for entry in substantiated
                if entry["test_case_id"] not in COMMON_CORE_IDS],
            # Recomputed over what this receipt actually publishes. Copying the
            # nightly summary would claim passes the receipt does not contain once
            # an unsubstantiated observation is omitted.
            "summary": _summarize([entry["status"] for entry in results], governed=True),
            "unsubstantiated": unsubstantiated,
        }

    def write_release_receipt(self, path: Path) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(self.build_release_receipt(), indent=2) + "\n")

    @property
    def has_records(self) -> bool:
        return bool(self._results) or bool(self._extensions)


def _prefer(candidate: CertResult, existing: CertResult, *, release: bool = False) -> bool:
    ranks = _RELEASE_STATUS_RANK if release else _STATUS_RANK
    candidate_rank = ranks.get(candidate.status, 0)
    existing_rank = ranks.get(existing.status, 0)
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


def _summarize(statuses: list[str], *, governed: bool = False) -> dict:
    """Aggregate one status list. ``governed`` selects the underscored token."""
    inapplicable = "not_applicable" if governed else "not-applicable"
    return {
        "total": len(statuses),
        "passed": sum(1 for value in statuses if value == "pass"),
        "failed": sum(1 for value in statuses if value == "fail"),
        "skipped": sum(1 for value in statuses if value == "skip"),
        "not_applicable": sum(1 for value in statuses if value == inapplicable),
    }


def _release_provenance(recorded: CertResult | None, client_id: str) -> dict | None:
    """The three provenance fields a governed result must carry, or ``None``.

    ``None`` means the observation cannot be published: it did not name the
    request it made, it did not name the governed facets it exercised, or it was
    performed by something other than the governed client. The caller omits it
    rather than filling the gap in.
    """
    if recorded is None:
        return None
    # `client_identity` is how a lane records "a different client made this
    # observation" -- several lanes record `httpx` for probes the library itself
    # cannot make. Stamping the governed client_id over that would publish exactly
    # the substitution the receipt contract prohibits, so omit instead.
    if recorded.client_identity and recorded.client_identity != client_id:
        return None
    facets = tuple(dict.fromkeys(recorded.exercised_capabilities))
    if not facets or len(facets) != len(recorded.exercised_capabilities):
        return None
    if recorded.status == "skip" and recorded.request_url is None:
        return {"performed_by": client_id, "request_url": None,
                "exercised_capabilities": list(facets)}
    if not _is_publishable_url(recorded.request_url):
        return None
    return {"performed_by": client_id, "request_url": recorded.request_url,
            "exercised_capabilities": list(facets)}


# Query parameters that commonly carry a secret. Receipts are uploaded as CI
# artifacts, so a URL bearing one must never be published -- and a client that
# authenticates through the query string leaves `urlparse().username` unset, so the
# userinfo check alone does not catch it. Kept in sync with CREDENTIAL_QUERY_KEYS in
# scripts/certification/verify-client-certification-receipts.py.
CREDENTIAL_QUERY_KEYS: frozenset[str] = frozenset({
    "access_token", "api_key", "apikey", "auth", "authorization", "code",
    "id_token", "key", "password", "pwd", "refresh_token", "secret", "session",
    "sig", "signature", "token", "x-api-key",
})


def _is_publishable_url(value: object) -> bool:
    """An absolute HTTP(S) URL that carries no credential in userinfo or query."""
    if not isinstance(value, str):
        return False
    parsed = urlparse(value)
    if parsed.scheme not in {"http", "https"} or not parsed.netloc:
        return False
    if parsed.username is not None or parsed.password is not None:
        return False
    return not any(
        key.strip().lower() in CREDENTIAL_QUERY_KEYS
        for key, _ in parse_qsl(parsed.query, keep_blank_values=True)
    )


def _as_dict(result: CertResult) -> dict:
    value = {
        "test_case_id": result.test_case_id,
        "status": result.status,
        "duration_ms": result.duration_ms,
        "measured_count": result.measured_count,
        "measured_delta": result.measured_delta,
        "notes": result.notes,
        "evidence_ref": result.evidence_ref,
        "protocol_version": result.protocol_version,
    }
    if result.client_identity:
        value["client_identity"] = result.client_identity
    return value


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
    """Assemble the receipt bindings shared by every canonical-client lane.

    The release-tier bindings are read from the environment the release lane sets
    and are left ``None`` everywhere else. They are never defaulted or inferred:
    ``build_release_receipt`` refuses to emit without them, which is the behaviour
    that stops a nightly or developer run from looking like a candidate-bound one.

    ``fixture_revision`` and ``server_config_revision`` are content digests by
    default, which is what this repository's own fixture policy requires. The
    governed denominator instead names *symbolic* revisions -- values such as
    ``docker/cng/seed.sql@{source_sha}`` and ``cog-1.0`` -- and the release join
    compares them exactly, so a receipt carrying digests would fail every cell with
    ``revision-mismatch``. The release lane therefore supplies the governed values
    through ``HONUA_FIXTURE_REVISION`` / ``HONUA_SERVER_CONFIG_REVISION``; the
    digests remain the default for every other tier.
    """
    normalized = base_url.rstrip("/")
    return LaneRuntime(
        base_url=normalized,
        environment="ci" if os.getenv("CI") else "local",
        server_version=read_server_version(
            normalized, override_env=version_env, api_key=api_key
        ),
        server_commit=read_server_commit(project_root, override_env=commit_env),
        fixture_revision=os.getenv("HONUA_FIXTURE_REVISION") or file_digest(fixture_path),
        server_config_revision=(
            os.getenv("HONUA_SERVER_CONFIG_REVISION") or file_digest(server_config_path)),
        image_digest=os.getenv("HONUA_CANDIDATE_IMAGE_DIGEST") or None,
        producer_source_sha=os.getenv("HONUA_PRODUCER_SOURCE_SHA") or None,
        auth_policy_revision=os.getenv("HONUA_AUTH_POLICY_REVISION") or None,
        deployment_target=os.getenv("HONUA_DEPLOYMENT_TARGET") or None,
    )
