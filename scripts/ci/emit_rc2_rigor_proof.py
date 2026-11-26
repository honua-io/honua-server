#!/usr/bin/env python3
"""Fail-closed adapter that builds candidate-bound RC2 rigor proof JSON artifacts.

Converts live verification scorecards (Track 1 AOT parity, Track 1 AOT soak,
PostGIS upgrade/degraded-startup, GIS clients/proxy, and tenant isolation) into
the exact JSON proof contracts validated by honua-release/tools/rc2_rigor.py.
Every required flag must be backed by a real passing observation; missing or
failed probes fail closed with a non-zero exit code.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from datetime import UTC, datetime
import json
from pathlib import Path
import re
import sys
from typing import Any

if __package__:
    from .rc2_candidate_image import verify_candidate_image_identity
else:
    from rc2_candidate_image import verify_candidate_image_identity

SHA_RE = re.compile(r"^[0-9a-f]{40}$")
DIGEST_RE = re.compile(r"^sha256:[0-9a-f]{64}$")
MIB_BYTES = 1024 * 1024
HTTP_OK = 200
MIN_SINGLE_TENANT_WORKFLOWS = 7

AOT_PROBE_FLAG_MAP: dict[str, tuple[str, ...]] = {
    "healthyReady": ("health_live", "health_ready"),
    "geoservicesJsonPbf": ("featureserver_json", "featureserver_pbf"),
    "mapExportSkia": ("mapserver_png_export",),
    "parquetNative": ("featureserver_parquet",),
    "ogcWeakEtag304": ("ogc_weak_etag_304",),
    "odataConditional": ("odata_conditional",),
    "wmsGetMap": ("wms_get_map",),
    "oidcOauthCallback": ("oidc_oauth_callback",),
}

SOAK_ROUTE_TO_WORKLOAD: dict[str, str] = {
    "featureserver_pbf": "featureserver-pbf",
    "featureserver_json": "featureserver-json",
    "ogc_features_items": "ogc-items",
    "ogc_tiles": "mvt-tiles",
    "mapserver_export": "skia-export",
}

REQUIRED_CLIENTS = frozenset({"qgis", "maplibre", "cesium", "geoservices"})
REQUIRED_CORS_ERROR_CODES = frozenset({400, 401, 402, 403, 429})
CLIENT_PROXY_FLAGS = (
    "forwardedProtoHttps",
    "ogcNextLinksHttps",
    "capabilitiesOnlineResourceHttps",
    "axisOrderVerified",
    "geoservicesErrorEnvelopeVerified",
)
REQUIRED_HOST_ADMIN_ROUTES = frozenset({
    "/api/v1/admin/connections",
    "/api/v1/admin/roles",
    "/api/v1/admin/rls-policies",
    "/api/v1/admin/field-mask-policies",
    "/api/v1/console/access/*",
    "/api/v1/admin/operations/streaming",
    "/api/v1/admin/observability/*",
    "/api/v1/admin/cache/*",
})
REQUIRED_HIDDEN_PROTOCOLS = frozenset({
    "geoservices",
    "ogc",
    "odata",
    "stac",
    "spatial-analytics",
})
UPGRADE_REQUIRED_FLAGS = (
    "degradedStartupSurvived",
    "degradedInitialNotReady",
    "editableAfterUpgrade",
)


@dataclass(frozen=True)
class CandidateIdentity:
    """Immutable RC candidate identifiers required in every proof artifact."""

    server_sha: str
    image_digest: str
    lock_digest: str
    observed_at: str


def validate_identity(identity: CandidateIdentity) -> dict[str, str]:
    """Validate candidate identifiers and return the shared proof header."""
    if not SHA_RE.fullmatch(identity.server_sha):
        raise ValueError("server_sha must be 40 lowercase hex characters")
    if not DIGEST_RE.fullmatch(identity.image_digest):
        raise ValueError("image_digest must be a sha256:<64 hex> digest")
    if not DIGEST_RE.fullmatch(identity.lock_digest):
        raise ValueError("lock_digest must be a sha256:<64 hex> digest")
    return {
        "serverSha": identity.server_sha,
        "imageDigest": identity.image_digest,
        "platformLockDigest": identity.lock_digest,
        "observedAt": identity.observed_at,
    }


def _probe_pass_map(scorecard: dict[str, Any]) -> dict[str, bool]:
    probes = scorecard.get("probes")
    if not isinstance(probes, list) or not probes:
        raise ValueError("parity scorecard must contain a non-empty probes list")
    return {str(item["name"]): item.get("passed") is True for item in probes}


def _arch_entry(scorecard: dict[str, Any], platform_digest: str) -> dict[str, Any]:
    if not DIGEST_RE.fullmatch(platform_digest):
        raise ValueError("platform_digest must be a sha256:<64 hex> digest")
    passed = _probe_pass_map(scorecard)
    flags = {
        flag: all(passed.get(name) is True for name in required)
        for flag, required in AOT_PROBE_FLAG_MAP.items()
    }
    missing = [flag for flag, ok in flags.items() if not ok]
    if missing:
        raise ValueError(f"AOT parity scorecard failed required flags: {', '.join(missing)}")
    summary = scorecard.get("summary") or {}
    reflection_errors = int(summary.get("reflection_error_count", 1))
    unhandled = int(summary.get("unhandled_exceptions", reflection_errors))
    if unhandled != 0:
        raise ValueError(f"AOT parity observed {unhandled} unhandled/reflection errors")
    return {**flags, "platformDigest": platform_digest, "unhandledExceptions": unhandled}


def build_aot_multiarch_proof(
    identity: CandidateIdentity,
    amd64_input: tuple[dict[str, Any], str],
    arm64_input: tuple[dict[str, Any], str],
) -> dict[str, Any]:
    """Build aot-multiarch-regression-proof.json from amd64 and arm64 scorecards."""
    header = validate_identity(identity)
    amd64_entry = _arch_entry(*amd64_input)
    arm64_entry = _arch_entry(*arm64_input)
    if amd64_entry["platformDigest"] == arm64_entry["platformDigest"]:
        raise ValueError("amd64 and arm64 platform digests must be distinct")
    return {
        **header,
        "architectures": {"amd64": amd64_entry, "arm64": arm64_entry},
    }


def _rss_bytes(summary: dict[str, Any], memory_limit_bytes: int) -> dict[str, int]:
    rss = summary.get("rss") or {}
    warmup_mb = rss.get("warmup_mean_mb")
    final_mb = rss.get("tail_mean_mb")
    peak_mb = rss.get("peak_mb", max(warmup_mb or 0, final_mb or 0))
    if not all(isinstance(v, (int, float)) and v > 0 for v in (warmup_mb, final_mb, peak_mb)):
        raise ValueError("soak scorecard is missing positive RSS measurements")
    warmup = int(round(warmup_mb * MIB_BYTES))
    final = int(round(final_mb * MIB_BYTES))
    peak = max(warmup, final, int(round(peak_mb * MIB_BYTES)))
    if peak >= memory_limit_bytes:
        raise ValueError("peak RSS reached or exceeded container memory limit")
    return {
        "memoryLimitBytes": memory_limit_bytes,
        "warmupRssBytes": warmup,
        "peakRssBytes": peak,
        "finalRssBytes": final,
    }


def _covered_workloads(per_route: dict[str, Any]) -> list[str]:
    covered: list[str] = []
    for route_name, workload in SOAK_ROUTE_TO_WORKLOAD.items():
        stats = per_route.get(route_name)
        if isinstance(stats, dict) and int(stats.get("requests", 0)) > 0:
            covered.append(workload)
    return sorted(covered)


def build_aot_soak_proof(
    identity: CandidateIdentity,
    scorecard: dict[str, Any],
    container_limits: dict[str, Any],
) -> dict[str, Any]:
    """Build aot-memory-soak-proof.json from a run-aot-soak.py scorecard."""
    header = validate_identity(identity)
    summary = scorecard.get("summary") or {}
    if summary.get("passed") is not True:
        raise ValueError("soak scorecard did not pass all gates")
    lat = summary.get("latency_ms") or {}
    memory_limit = int(container_limits["memoryLimitBytes"])
    rss_fields = _rss_bytes(summary, memory_limit)
    workloads = _covered_workloads(summary.get("per_route") or {})
    if len(workloads) < len(SOAK_ROUTE_TO_WORKLOAD):
        raise ValueError(f"soak missed required workloads; got {workloads}")
    return {
        **header,
        "durationSeconds": float(summary["elapsed_s"]),
        **rss_fields,
        "swapDisabled": bool(container_limits["swapDisabled"]),
        "oomKillCount": int(container_limits["oomKillCount"]),
        "http5xxCount": int(summary.get("http_5xx_count", summary.get("error_count", 0))),
        "totalRequests": int(summary["total_requests"]),
        "workloadsCovered": workloads,
        "p50LatencyMs": float(lat["p50"]),
        "p95LatencyMs": float(lat["p95"]),
        "p99LatencyMs": float(lat["p99"]),
    }


def build_postgis_upgrade_proof(
    identity: CandidateIdentity, observation: dict[str, Any]
) -> dict[str, Any]:
    """Build postgis-upgrade-recovery-proof.json from a live upgrade drill observation."""
    header = validate_identity(identity)
    prior = str(observation.get("priorRcImageDigest", ""))
    if not DIGEST_RE.fullmatch(prior) or prior == identity.image_digest:
        raise ValueError("priorRcImageDigest must be a distinct sha256 digest")
    pre = int(observation.get("preUpgradeFeatureCount", 0))
    post = int(observation.get("postUpgradeFeatureCount", 0))
    if pre <= 0 or post < pre:
        raise ValueError("upgrade lost features or lacked positive pre-upgrade count")
    if int(observation.get("migrationLockFailures", 1)) != 0:
        raise ValueError("upgrade observed migration lock failures")
    if int(observation.get("recoveredReadyHttpStatus", 0)) != HTTP_OK:
        raise ValueError(f"degraded startup did not recover to HTTP {HTTP_OK}")
    missing = [flag for flag in UPGRADE_REQUIRED_FLAGS if observation.get(flag) is not True]
    if missing:
        raise ValueError(f"upgrade observation failed required flags: {', '.join(missing)}")
    return {
        **header,
        "priorRcImageDigest": prior,
        "preUpgradeFeatureCount": pre,
        "postUpgradeFeatureCount": post,
        "migrationLockFailures": 0,
        "recoveredReadyHttpStatus": HTTP_OK,
        **{flag: True for flag in UPGRADE_REQUIRED_FLAGS},
    }


def build_gis_clients_proxy_proof(
    identity: CandidateIdentity, observation: dict[str, Any]
) -> dict[str, Any]:
    """Build gis-clients-proxy-proof.json from a live client and HTTPS proxy observation."""
    header = validate_identity(identity)
    clients = observation.get("clients")
    if not isinstance(clients, dict) or not REQUIRED_CLIENTS <= set(clients):
        raise ValueError("clients observation missing required GIS clients")
    passed_clients = {name: _validated_client(name, clients[name]) for name in sorted(REQUIRED_CLIENTS)}
    cors_codes = sorted({int(code) for code in (observation.get("corsOnErrorCodes") or [])})
    if not REQUIRED_CORS_ERROR_CODES <= set(cors_codes):
        raise ValueError(f"corsOnErrorCodes missing required status codes: {cors_codes}")
    missing = [flag for flag in CLIENT_PROXY_FLAGS if observation.get(flag) is not True]
    if missing:
        raise ValueError(f"proxy observation failed required flags: {', '.join(missing)}")
    return {
        **header,
        "clients": passed_clients,
        "corsOnErrorCodes": cors_codes,
        **{flag: True for flag in CLIENT_PROXY_FLAGS},
    }


def _validated_client(name: str, entry: Any) -> dict[str, str]:
    if not isinstance(entry, dict) or entry.get("status") != "PASS":
        raise ValueError(f"client {name} did not record PASS with evidence")
    evidence = entry.get("evidence")
    if not isinstance(evidence, str) or not evidence.strip():
        raise ValueError(f"client {name} did not record PASS with evidence")
    return {"status": "PASS", "evidence": evidence.strip()}


def build_tenant_isolation_proof(
    identity: CandidateIdentity, observation: dict[str, Any]
) -> dict[str, Any]:
    """Build tenant-isolation-parity-proof.json from single/multi-tenant probe observations."""
    header = validate_identity(identity)
    if observation.get("singleTenantConsoleZero403") is not True or observation.get("platformAdminSucceeded") is not True:
        raise ValueError("single-tenant parity or platform-admin verification failed")
    workflows = int(observation.get("singleTenantWorkflowsPassed", 0))
    if workflows < MIN_SINGLE_TENANT_WORKFLOWS:
        raise ValueError(f"singleTenantWorkflowsPassed must be >= {MIN_SINGLE_TENANT_WORKFLOWS}, got {workflows}")
    if int(observation.get("tenantAdminAllowedHostRoutes", 1)) != 0:
        raise ValueError("tenant admin was allowed on host-admin route")
    if int(observation.get("crossTenantLeaks", 1)) != 0:
        raise ValueError("cross-tenant leak detected")
    routes = sorted({str(r) for r in (observation.get("multiTenantPlatformAdminRequiredRoutes") or [])})
    protocols = sorted({str(p) for p in (observation.get("hiddenFieldProtocolsBlocked") or [])})
    if not REQUIRED_HOST_ADMIN_ROUTES <= set(routes) or not REQUIRED_HIDDEN_PROTOCOLS <= set(protocols):
        raise ValueError("tenant observation missing required host routes or hidden-field protocols")
    return {
        **header,
        "singleTenantConsoleZero403": True,
        "platformAdminSucceeded": True,
        "singleTenantWorkflowsPassed": workflows,
        "tenantAdminAllowedHostRoutes": 0,
        "crossTenantLeaks": 0,
        "multiTenantPlatformAdminRequiredRoutes": routes,
        "hiddenFieldProtocolsBlocked": protocols,
    }


def _load_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def _write_json(path: Path, doc: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")


def _add_common_args(cmd: argparse.ArgumentParser) -> None:
    cmd.add_argument("--server-sha", required=True)
    cmd.add_argument("--image-digest", required=True)
    cmd.add_argument("--lock-digest", required=True)
    cmd.add_argument("--observed-at", default=datetime.now(UTC).isoformat().replace("+00:00", "Z"))
    cmd.add_argument("--out", type=Path, required=True)


def _parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="kind", required=True)
    verify = sub.add_parser("verify-image")
    verify.add_argument("--server-sha", required=True)
    verify.add_argument("--image-digest", required=True)
    verify.add_argument("--arch", choices=("amd64", "arm64"), required=True)
    verify.add_argument("--platform-digest", required=True)
    for name in ("aot-multiarch", "aot-soak", "postgis-upgrade", "gis-clients-proxy", "tenant-isolation"):
        cmd = sub.add_parser(name)
        _add_common_args(cmd)
        if name == "aot-multiarch":
            cmd.add_argument("--amd64-scorecard", type=Path, required=True)
            cmd.add_argument("--amd64-digest", required=True)
            cmd.add_argument("--arm64-scorecard", type=Path, required=True)
            cmd.add_argument("--arm64-digest", required=True)
        elif name == "aot-soak":
            cmd.add_argument("--soak-scorecard", type=Path, required=True)
            cmd.add_argument("--memory-limit-bytes", type=int, required=True)
            cmd.add_argument("--swap-disabled", action="store_true")
            cmd.add_argument("--oom-kill-count", type=int, default=0)
        else:
            cmd.add_argument("--observation", type=Path, required=True)
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    """CLI entry point returning 0 on valid proof emission and 1 otherwise."""
    args = _parse_args(argv)
    if args.kind == "verify-image":
        verify_candidate_image_identity(args.server_sha, args.image_digest, (args.arch, args.platform_digest))
        return 0
    identity = CandidateIdentity(args.server_sha, args.image_digest, args.lock_digest, args.observed_at)
    if args.kind == "aot-multiarch":
        proof = build_aot_multiarch_proof(
            identity,
            (_load_json(args.amd64_scorecard), args.amd64_digest),
            (_load_json(args.arm64_scorecard), args.arm64_digest),
        )
    elif args.kind == "aot-soak":
        limits = {
            "memoryLimitBytes": args.memory_limit_bytes,
            "swapDisabled": args.swap_disabled,
            "oomKillCount": args.oom_kill_count,
        }
        proof = build_aot_soak_proof(identity, _load_json(args.soak_scorecard), limits)
    else:
        builders = {
            "postgis-upgrade": build_postgis_upgrade_proof,
            "gis-clients-proxy": build_gis_clients_proxy_proof,
            "tenant-isolation": build_tenant_isolation_proof,
        }
        proof = builders[args.kind](identity, _load_json(args.observation))
    _write_json(args.out, proof)
    return 0


if __name__ == "__main__":
    sys.exit(main())
