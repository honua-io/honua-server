#!/usr/bin/env python3
"""Verify a live NativeAOT Honua Server container across protocols and error paths."""

from __future__ import annotations

import argparse
import json
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path

from aot_parity_probes import (
    BINARY_VALIDATORS,
    SCHEMA_VERSION,
    HttpReply,
    ParityConfig,
    ProbeResult,
    ProbeSpec,
    build_probes,
    scan_reflection_errors,
    validate_reply,
)


@dataclass(frozen=True)
class DockerSession:
    """Ephemeral Docker network and containers booted for --image mode."""

    network: str
    pg_container: str
    app_container: str
    base_url: str


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


def _allocate_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


REPO_ROOT = Path(__file__).resolve().parents[2]
SEED_SQL_PATHS = (
    REPO_ROOT / "docker" / "cng" / "seed.sql",
    REPO_ROOT / "scripts" / "ci" / "seed-serving-formats.sql",
)


def _run_docker_checked(args: list[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(["docker", *args], capture_output=True, text=True, check=True)


def _run_docker_unchecked(args: list[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(["docker", *args], capture_output=True, text=True, check=False)


def _apply_seed_sql(pg_container: str, sql_path: Path) -> None:
    sql_text = sql_path.read_text(encoding="utf-8")
    subprocess.run(
        ["docker", "exec", "-i", pg_container, "psql", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "honua_cng"],
        input=sql_text,
        capture_output=True,
        text=True,
        check=True,
    )


def _seed_cng_fixtures(session: DockerSession, boot_timeout: float) -> None:
    _run_docker_checked(["stop", session.app_container])
    for sql_path in SEED_SQL_PATHS:
        _apply_seed_sql(session.pg_container, sql_path)
    _run_docker_checked(["start", session.app_container])
    _wait_for_ready(session.base_url, boot_timeout)


def start_container_stack(cfg: ParityConfig) -> DockerSession:
    """Boot PostGIS and the target NativeAOT image, seed CNG fixtures, and wait ready."""
    assert cfg.image is not None
    tag = f"honua-aot-parity-{int(time.time())}-{_allocate_port()}"
    port = _allocate_port()
    session = DockerSession(f"{tag}-net", f"{tag}-pg", f"{tag}-app", f"http://127.0.0.1:{port}")
    _run_docker_checked(["network", "create", session.network])
    try:
        _start_postgis_container(session.network, session.pg_container, cfg.postgis_image)
        _wait_for_postgis(session.pg_container, cfg.boot_timeout)
        _start_app_container(session, cfg.image, port)
        _wait_for_ready(session.base_url, cfg.boot_timeout)
        _seed_cng_fixtures(session, cfg.boot_timeout)
        return session
    except Exception:
        stop_container_stack(session)
        raise


def _start_postgis_container(network: str, name: str, image: str) -> None:
    _run_docker_checked([
        "run", "-d", "--name", name, "--network", network,
        "-e", "POSTGRES_DB=honua_cng", "-e", "POSTGRES_USER=postgres",
        "-e", "POSTGRES_PASSWORD=cng_password", image,
    ])


def _start_app_container(session: DockerSession, image: str, port: int) -> None:
    conn = (
        f"Server={session.pg_container};Port=5432;"
        "Database=honua_cng;User Id=postgres;Password=cng_password;"
    )
    _run_docker_checked([
        "run", "-d", "--name", session.app_container, "--network", session.network,
        "-p", f"127.0.0.1:{port}:8080", "--read-only", "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges:true",
        "--tmpfs", "/tmp/honua:rw,noexec,nosuid,size=256m,uid=1001,gid=1001,mode=750",
        "-e", "ASPNETCORE_ENVIRONMENT=Test",
        "-e", "HONUA_REGISTER_TEST_INFRASTRUCTURE=true",
        "-e", "Experimental__Features__RedshiftProvider=false",
        "-e", "Experimental__Features__SnowflakeProvider=false",
        "-e", "Experimental__Features__DatabricksProvider=false",
        "-e", "DataSource__Provider=postgres",
        "-e", f"ConnectionStrings__DefaultConnection={conn}",
        "-e", f"ConnectionStrings__honua={conn}",
        "-e", "HONUA_ADMIN_PASSWORD=AotParityAdminPassword123!", image,
    ])


def _wait_for_postgis(pg_name: str, timeout: float) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        proc = _run_docker_unchecked(["exec", pg_name, "pg_isready", "-U", "postgres"])
        if proc.returncode == 0:
            return
        time.sleep(1.0)
    raise RuntimeError(f"PostGIS container {pg_name} did not become ready within {timeout}s")


def _is_endpoint_ready(url: str) -> bool:
    try:
        with urllib.request.urlopen(url, timeout=3.0) as resp:
            return resp.status == 200
    except (urllib.error.URLError, TimeoutError, OSError):
        return False


def _wait_for_ready(base_url: str, timeout: float) -> None:
    deadline = time.monotonic() + timeout
    url = base_url.rstrip("/") + "/healthz/ready"
    while time.monotonic() < deadline:
        if _is_endpoint_ready(url):
            return
        time.sleep(1.0)
    raise RuntimeError(f"NativeAOT server at {url} did not become ready within {timeout}s")


def stop_container_stack(session: DockerSession) -> str:
    """Capture app container logs and tear down ephemeral containers and network."""
    logs = _run_docker_unchecked(["logs", session.app_container])
    for name in (session.app_container, session.pg_container):
        _run_docker_unchecked(["rm", "-f", name])
    _run_docker_unchecked(["network", "rm", session.network])
    return (logs.stdout or "") + "\n" + (logs.stderr or "")


def collect_logs(cfg: ParityConfig, session_logs: str) -> str:
    """Gather stderr/stdout logs from ephemeral session, named container, or log file."""
    chunks = [session_logs]
    if cfg.container_name:
        proc = _run_docker_unchecked(["logs", cfg.container_name])
        chunks.extend([proc.stderr or "", proc.stdout or ""])
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
            session = start_container_stack(cfg)
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


def _execute_probes(base_url: str, cfg: ParityConfig) -> tuple[list[ProbeResult], list[str]]:
    results: list[ProbeResult] = []
    body_errors: list[str] = []
    for spec in build_probes(cfg):
        reply = send_probe(base_url, spec, cfg)
        if spec.validator not in BINARY_VALIDATORS:
            body_errors.extend(scan_reflection_errors(reply.body.decode("utf-8", errors="replace")))
        passed, detail = validate_reply(spec, reply)
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
