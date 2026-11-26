#!/usr/bin/env python3
"""Verify a live NativeAOT Honua Server container across protocols and error paths."""

from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path

from aot_container_fixture import (
    ContainerOptions,
    DockerSession,
    read_container_logs,
    start_container_stack,
    stop_container_stack,
)
from aot_parity_probes import (
    BINARY_VALIDATORS,
    CONDITIONAL_VALIDATORS,
    SCHEMA_VERSION,
    HttpReply,
    ParityConfig,
    ProbeResult,
    ProbeSpec,
    build_probes,
    scan_reflection_errors,
    validate_reply,
)


HTTP_OK = 200
HTTP_NOT_MODIFIED = 304
PARITY_ENVIRONMENT = (
    "Oidc__Enabled=true",
    "Oidc__AzureAd__Enabled=true",
    "Oidc__AzureAd__TenantId=00000000-0000-0000-0000-000000000001",
    "Oidc__AzureAd__ClientId=00000000-0000-0000-0000-000000000002",
    "HONUA_ADMIN_PASSWORD=AotParityAdminPassword123!",
)


@dataclass(frozen=True)
class ParityRunResult:
    """Aggregated probe outcomes and reflection errors for scorecard building."""

    base_url: str
    results: list[ProbeResult]
    reflection_errors: list[str]


def send_probe(base_url: str, spec: ProbeSpec, cfg: ParityConfig) -> HttpReply:
    """Execute one HTTP probe and capture status, headers, body, and latency."""
    url = base_url.rstrip("/") + spec.path
    headers = dict(spec.headers)
    if cfg.api_key and spec.name != "error_401_unauthorized":
        headers.setdefault("X-API-Key", cfg.api_key)
    req = urllib.request.Request(url, data=spec.body, headers=headers, method=spec.method)
    start = time.perf_counter()
    try:
        with urllib.request.urlopen(req, timeout=cfg.timeout) as resp:
            hdrs = {k.lower(): v for k, v in resp.headers.items()}
            return HttpReply(resp.status, hdrs, resp.read(), _elapsed_ms(start))
    except urllib.error.HTTPError as exc:
        hdrs = {k.lower(): v for k, v in exc.headers.items()} if exc.headers else {}
        return HttpReply(exc.code, hdrs, exc.read(), _elapsed_ms(start))
    except (urllib.error.URLError, TimeoutError, OSError) as exc:
        return HttpReply(0, {}, b"", _elapsed_ms(start), error=str(exc))


def _elapsed_ms(start: float) -> float:
    return round((time.perf_counter() - start) * 1000.0, 2)


def collect_logs(cfg: ParityConfig, session_logs: str) -> str:
    """Gather stderr/stdout logs from ephemeral session, named container, or log file."""
    chunks = [session_logs]
    if cfg.container_name:
        chunks.append(read_container_logs(cfg.container_name))
    if cfg.log_file and cfg.log_file.exists():
        chunks.append(cfg.log_file.read_text(encoding="utf-8", errors="replace"))
    return "\n".join(chunks)


def run_parity_suite(cfg: ParityConfig) -> dict[str, object]:
    """Run all parity probes and return the structured scorecard payload."""
    session: DockerSession | None = None
    session_logs = ""
    base_url = cfg.base_url
    try:
        if cfg.image and not base_url:
            session = start_container_stack(
                cfg.image, cfg.postgis_image, ContainerOptions(cfg.boot_timeout, PARITY_ENVIRONMENT),
            )
            base_url = session.base_url
        if not base_url:
            raise ValueError("Either --base-url or --image must be provided")
        results, body_errors = _execute_probes(base_url, cfg)
    finally:
        if session is not None:
            session_logs = stop_container_stack(session)
    log_errors = scan_reflection_errors(collect_logs(cfg, session_logs))
    run = ParityRunResult(base_url, results, list(dict.fromkeys(body_errors + log_errors)))
    return build_scorecard(cfg, run)


def _run_conditional_probe(
    base_url: str, spec: ProbeSpec, cfg: ParityConfig
) -> tuple[HttpReply, bool, str]:
    first = send_probe(base_url, spec, cfg)
    if first.error or first.status != HTTP_OK:
        detail = first.error or f"initial GET expected HTTP {HTTP_OK}, got {first.status}"
        return first, False, detail
    etag = first.headers.get("etag", "").strip()
    if not etag:
        return first, False, "initial GET response missing ETag header"
    if spec.validator == "conditional_weak_etag_304" and not etag.startswith('W/"'):
        return first, False, f"expected weak ETag starting with W/\", got {etag!r}"
    cond_spec = ProbeSpec(
        spec.name, spec.category, spec.method, spec.path,
        spec.expected_status, spec.validator, headers={**spec.headers, "If-None-Match": etag},
    )
    second = send_probe(base_url, cond_spec, cfg)
    if second.error:
        return second, False, f"conditional GET transport error: {second.error}"
    if second.status != HTTP_NOT_MODIFIED:
        return second, False, f"conditional GET expected HTTP {HTTP_NOT_MODIFIED}, got {second.status}"
    return second, True, f"ok (etag={etag})"


def _execute_probes(base_url: str, cfg: ParityConfig) -> tuple[list[ProbeResult], list[str]]:
    results: list[ProbeResult] = []
    body_errors: list[str] = []
    for spec in build_probes(cfg):
        if spec.validator in CONDITIONAL_VALIDATORS:
            reply, passed, detail = _run_conditional_probe(base_url, spec, cfg)
        else:
            reply = send_probe(base_url, spec, cfg)
            passed, detail = validate_reply(spec, reply)
        if spec.validator not in BINARY_VALIDATORS:
            body_errors.extend(scan_reflection_errors(reply.body.decode("utf-8", errors="replace")))
        results.append(
            ProbeResult(
                spec.name, spec.category, spec.method, spec.path,
                reply.status, spec.expected_status, reply.latency_ms, passed, detail,
            )
        )
    return results, body_errors


def build_scorecard(cfg: ParityConfig, run: ParityRunResult) -> dict[str, object]:
    """Build the honua.eval.aot-runtime-parity.v1 scorecard dictionary."""
    total = len(run.results)
    passed_count = sum(1 for item in run.results if item.passed)
    failed_count = total - passed_count
    all_probes_passed = total > 0 and failed_count == 0
    zero_reflection = len(run.reflection_errors) == 0
    return {
        "schema": SCHEMA_VERSION,
        "timestamp_utc": datetime.now(UTC).isoformat(),
        "target": {"base_url": run.base_url, "image": cfg.image, "container_name": cfg.container_name},
        "summary": {
            "total_probes": total,
            "passed_probes": passed_count,
            "failed_probes": failed_count,
            "pass_rate_pct": round((passed_count / total) * 100.0, 2) if total else 0.0,
            "reflection_error_count": len(run.reflection_errors),
            "passed": all_probes_passed and zero_reflection,
        },
        "gates": {"all_probes_passed": all_probes_passed, "zero_reflection_errors": zero_reflection},
        "probes": [item.__dict__ for item in run.results],
        "reflection_errors": run.reflection_errors,
    }


def parse_args(argv: list[str] | None = None) -> ParityConfig:
    """Parse CLI arguments into a ParityConfig."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", help="Base URL of a running NativeAOT server")
    parser.add_argument("--image", help="Docker image tag/digest to boot with PostGIS")
    parser.add_argument("--container-name", help="Running container name for log scanning")
    parser.add_argument("--log-file", type=Path, help="Optional stderr log file to scan")
    parser.add_argument("--service-id", default="cng")
    parser.add_argument("--map-service-id", default="cng-map")
    parser.add_argument("--layer-id", default="1000")
    parser.add_argument("--timeout", type=float, default=15.0)
    parser.add_argument("--boot-timeout", type=float, default=120.0)
    parser.add_argument("--postgis-image", default="postgis/postgis:16-3.4")
    parser.add_argument("--api-key", default=None)
    parser.add_argument("--scorecard", type=Path, default=None)
    ns = parser.parse_args(argv)
    if not ns.base_url and not ns.image:
        parser.error("one of --base-url or --image is required")
    return ParityConfig(**vars(ns))


def main(argv: list[str] | None = None) -> int:
    """CLI entry point returning 0 on full parity pass and 1 otherwise."""
    cfg = parse_args(argv)
    scorecard = run_parity_suite(cfg)
    payload = json.dumps(scorecard, indent=2)
    if cfg.scorecard:
        cfg.scorecard.parent.mkdir(parents=True, exist_ok=True)
        cfg.scorecard.write_text(payload + "\n", encoding="utf-8")
    print(payload)
    summary = scorecard["summary"]
    assert isinstance(summary, dict)
    return 0 if summary["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
