#!/usr/bin/env python3
"""Run a mixed-load NativeAOT soak test and gate on latency, errors, and RSS drift."""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from collections.abc import Callable
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from pathlib import Path

from aot_soak_metrics import (
    RequestSample,
    RouteSpec,
    RssSample,
    SoakConfig,
    SoakOutcome,
    build_scorecard,
    parse_mem_usage_mb,
)

PNG_MAGIC = b"\x89PNG\r\n\x1a\n"


@dataclass(frozen=True)
class SoakDeps:
    """Injectable container samplers for unit testing without Docker."""

    rss_reader: Callable[[str], float | None]
    restart_reader: Callable[[str], int | None]


def build_routes(cfg: SoakConfig) -> list[RouteSpec]:
    """Build the mixed-protocol soak route list."""
    fs = f"/rest/services/{cfg.service_id}/FeatureServer/{cfg.layer_id}/query"
    q = "where=1%3D1&outFields=*"
    ms = (
        f"/rest/services/{cfg.map_service_id}/MapServer/export"
        "?bbox=-180,-90,180,90&bboxSR=4326&size=256,256&format=png&f=image"
    )
    return [
        RouteSpec("featureserver_json", f"{fs}?{q}&f=json"),
        RouteSpec("featureserver_pbf", f"{fs}?{q}&f=pbf"),
        RouteSpec("mapserver_export", ms, expect_png=True),
        RouteSpec("ogc_api_features", "/ogc/features/collections"),
        RouteSpec("odata", "/odata"),
        RouteSpec("health_ready", "/healthz/ready"),
    ]


def sample_container_rss(container_name: str) -> float | None:
    """Sample current container RSS in MiB via `docker stats --no-stream`."""
    proc = subprocess.run(
        ["docker", "stats", "--no-stream", "--format", "{{.MemUsage}}", container_name],
        capture_output=True,
        text=True,
        check=False,
    )
    if proc.returncode != 0:
        return None
    return parse_mem_usage_mb(proc.stdout.strip())


def read_restart_count(container_name: str) -> int | None:
    """Read Docker container RestartCount via `docker inspect`, or None on failure."""
    proc = subprocess.run(
        ["docker", "inspect", "--format", "{{.RestartCount}}", container_name],
        capture_output=True,
        text=True,
        check=False,
    )
    if proc.returncode != 0:
        return None
    text = proc.stdout.strip()
    return int(text) if text.isdigit() else None


def default_deps() -> SoakDeps:
    """Return production Docker-backed container samplers."""
    return SoakDeps(rss_reader=sample_container_rss, restart_reader=read_restart_count)


def execute_request(cfg: SoakConfig, route: RouteSpec) -> RequestSample:
    """Execute one soak HTTP GET request and validate status/PNG magic."""
    url = cfg.base_url.rstrip("/") + route.path
    start = time.perf_counter()
    try:
        with urllib.request.urlopen(url, timeout=cfg.timeout) as resp:
            body = resp.read()
            ok = 200 <= resp.status < 300 and (
                not route.expect_png or body.startswith(PNG_MAGIC)
            )
            return RequestSample(route.name, resp.status, _elapsed_ms(start), ok)
    except urllib.error.HTTPError as exc:
        exc.read()
        return RequestSample(route.name, exc.code, _elapsed_ms(start), False)
    except (urllib.error.URLError, TimeoutError, OSError):
        return RequestSample(route.name, 0, _elapsed_ms(start), False)


def _elapsed_ms(start: float) -> float:
    return round((time.perf_counter() - start) * 1000.0, 2)


def _worker_loop(
    worker_id: int, cfg: SoakConfig, deadline: float
) -> list[RequestSample]:
    routes = build_routes(cfg)
    recorded: list[RequestSample] = []
    index = worker_id % len(routes)
    while time.monotonic() < deadline:
        recorded.append(execute_request(cfg, routes[index]))
        index = (index + 1) % len(routes)
    return recorded


def _rss_sampler_loop(
    cfg: SoakConfig, deps: SoakDeps, stop_event: threading.Event
) -> list[RssSample]:
    if not cfg.container_name:
        return []
    samples: list[RssSample] = []
    start = time.monotonic()
    while not stop_event.is_set():
        rss = deps.rss_reader(cfg.container_name)
        if rss is not None:
            samples.append(RssSample(round(time.monotonic() - start, 2), rss))
        stop_event.wait(max(0.05, cfg.sample_interval))
    return samples


def run_soak(cfg: SoakConfig, deps: SoakDeps | None = None) -> SoakOutcome:
    """Execute concurrent mixed-load soak traffic with optional RSS sampling."""
    active_deps = deps or default_deps()
    start = time.monotonic()
    deadline = start + max(0.05, cfg.duration)
    stop_event = threading.Event()
    rss_holder: list[list[RssSample]] = [[]]

    def collect_rss() -> None:
        rss_holder[0] = _rss_sampler_loop(cfg, active_deps, stop_event)

    sampler = threading.Thread(target=collect_rss, daemon=True)
    sampler.start()
    samples: list[RequestSample] = []
    try:
        with ThreadPoolExecutor(max_workers=max(1, cfg.concurrency)) as pool:
            futures = [
                pool.submit(_worker_loop, idx, cfg, deadline)
                for idx in range(max(1, cfg.concurrency))
            ]
            for fut in futures:
                samples.extend(fut.result())
    finally:
        stop_event.set()
        sampler.join(timeout=5.0)
    restarts = active_deps.restart_reader(cfg.container_name) if cfg.container_name else 0
    return SoakOutcome(samples, rss_holder[0], restarts, round(time.monotonic() - start, 2))


def parse_args(argv: list[str] | None = None) -> SoakConfig:
    """Parse CLI arguments into a SoakConfig."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--container-name", default=None)
    parser.add_argument("--duration", type=float, default=600.0)
    parser.add_argument("--concurrency", type=int, default=8)
    parser.add_argument("--max-rss-drift-pct", type=float, default=5.0)
    parser.add_argument("--max-error-rate", type=float, default=0.0)
    parser.add_argument("--sample-interval", type=float, default=5.0)
    parser.add_argument("--service-id", default="cng")
    parser.add_argument("--map-service-id", default="cng-map")
    parser.add_argument("--layer-id", default="1000")
    parser.add_argument("--timeout", type=float, default=15.0)
    parser.add_argument("--scorecard", type=Path, default=None)
    ns = parser.parse_args(argv)
    return SoakConfig(**vars(ns))


def main(argv: list[str] | None = None) -> int:
    """CLI entry point returning 0 when all soak gates pass and 1 otherwise."""
    cfg = parse_args(argv)
    outcome = run_soak(cfg)
    scorecard = build_scorecard(cfg, outcome)
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
