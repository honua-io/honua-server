"""Soak metrics, RSS drift analysis, and scorecard construction for NativeAOT soak runs."""

from __future__ import annotations

import math
import re
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path

SCHEMA_VERSION = "honua.eval.aot-soak.v1"
MEM_USAGE_RE = re.compile(r"^\s*([0-9]+(?:\.[0-9]+)?)\s*([A-Za-z]+)")
UNIT_TO_MIB = {
    "b": 1.0 / (1024.0 * 1024.0),
    "kb": 1.0 / 1024.0,
    "kib": 1.0 / 1024.0,
    "mb": 1.0,
    "mib": 1.0,
    "gb": 1024.0,
    "gib": 1024.0,
}


@dataclass(frozen=True)
class SoakConfig:
    """Configuration for a NativeAOT mixed-load soak run."""

    base_url: str
    container_name: str | None = None
    duration: float = 600.0
    concurrency: int = 8
    max_rss_drift_pct: float = 5.0
    max_error_rate: float = 0.0
    sample_interval: float = 5.0
    service_id: str = "cng"
    map_service_id: str = "cng-map"
    layer_id: str = "1000"
    timeout: float = 15.0
    scorecard: Path | None = None


@dataclass(frozen=True)
class RouteSpec:
    """Single traffic route exercised during soak testing."""

    name: str
    path: str
    expect_png: bool = False


@dataclass(frozen=True)
class RequestSample:
    """Recorded outcome for a single HTTP soak request."""

    route: str
    status_code: int
    latency_ms: float
    ok: bool


@dataclass(frozen=True)
class RssSample:
    """Single timestamped container RSS observation in MiB."""

    elapsed_s: float
    rss_mb: float


@dataclass(frozen=True)
class RssAnalysis:
    """Summary statistics for container RSS drift and linear slope."""

    sample_count: int
    warmup_mean_mb: float | None
    tail_mean_mb: float | None
    peak_mb: float | None
    drift_pct: float | None
    slope_mb_per_min: float | None


@dataclass(frozen=True)
class SoakOutcome:
    """Collected traffic, memory, and restart observations from a soak run."""

    samples: list[RequestSample]
    rss_samples: list[RssSample]
    restart_count: int | None
    elapsed_s: float


def parse_mem_usage_mb(raw: str) -> float | None:
    """Parse the RSS field from `docker stats --no-stream --format '{{.MemUsage}}'`."""
    first_part = raw.split("/", 1)[0].strip()
    match = MEM_USAGE_RE.match(first_part)
    if not match:
        return None
    value = float(match.group(1))
    factor = UNIT_TO_MIB.get(match.group(2).lower())
    if factor is None:
        return None
    return round(value * factor, 3)


def percentile(sorted_values: list[float], pct: float) -> float:
    """Compute a linear-interpolated percentile from a sorted list of floats."""
    if not sorted_values:
        return 0.0
    rank = (pct / 100.0) * (len(sorted_values) - 1)
    low = math.floor(rank)
    high = math.ceil(rank)
    if low == high:
        return round(sorted_values[low], 2)
    weight = rank - low
    interpolated = sorted_values[low] * (1.0 - weight) + sorted_values[high] * weight
    return round(interpolated, 2)


def analyze_rss_drift(samples: list[RssSample], duration: float) -> RssAnalysis:
    """Compare warmup baseline (min 2-3) vs tail (final 2 min) and linear slope."""
    if not samples:
        return RssAnalysis(0, None, None, None, None, None)
    peak_mb = round(max(s.rss_mb for s in samples), 3)
    if len(samples) == 1:
        val = round(samples[0].rss_mb, 3)
        return RssAnalysis(1, val, val, peak_mb, 0.0, 0.0)
    warmup_vals, tail_vals, post_warmup = _partition_rss_windows(samples, duration)
    warmup_mean = sum(warmup_vals) / len(warmup_vals)
    tail_mean = sum(tail_vals) / len(tail_vals)
    drift_pct = ((tail_mean - warmup_mean) / warmup_mean) * 100.0 if warmup_mean > 0 else 0.0
    slope = _linear_slope_per_min(post_warmup)
    return RssAnalysis(
        sample_count=len(samples),
        warmup_mean_mb=round(warmup_mean, 3),
        tail_mean_mb=round(tail_mean, 3),
        peak_mb=peak_mb,
        drift_pct=round(drift_pct, 3),
        slope_mb_per_min=round(slope, 4),
    )


def _partition_rss_windows(
    samples: list[RssSample], duration: float
) -> tuple[list[float], list[float], list[RssSample]]:
    if duration >= 300.0:
        w_start, w_end = 120.0, 180.0
        t_start = max(w_end, duration - 120.0)
    else:
        span = max(duration, samples[-1].elapsed_s, 1.0)
        w_start, w_end = span * 0.20, span * 0.35
        t_start = span * 0.65
    warmup = [s.rss_mb for s in samples if w_start <= s.elapsed_s <= w_end]
    tail = [s.rss_mb for s in samples if s.elapsed_s >= t_start]
    post = [s for s in samples if s.elapsed_s >= w_start]
    if not warmup or not tail:
        third = max(1, len(samples) // 3)
        warmup = [s.rss_mb for s in samples[:third]]
        tail = [s.rss_mb for s in samples[-third:]]
        post = samples
    return warmup, tail, post


def _linear_slope_per_min(samples: list[RssSample]) -> float:
    if len(samples) < 2:
        return 0.0
    xs = [s.elapsed_s / 60.0 for s in samples]
    ys = [s.rss_mb for s in samples]
    mean_x = sum(xs) / len(xs)
    mean_y = sum(ys) / len(ys)
    denom = sum((x - mean_x) ** 2 for x in xs)
    if denom <= 0.0:
        return 0.0
    numer = sum((x - mean_x) * (y - mean_y) for x, y in zip(xs, ys))
    return numer / denom


def _summarize_routes(samples: list[RequestSample]) -> dict[str, dict[str, object]]:
    grouped: dict[str, list[RequestSample]] = {}
    for sample in samples:
        grouped.setdefault(sample.route, []).append(sample)
    summary: dict[str, dict[str, object]] = {}
    for name, items in sorted(grouped.items()):
        lats = sorted(item.latency_ms for item in items)
        errors = sum(1 for item in items if not item.ok)
        summary[name] = {
            "requests": len(items),
            "errors": errors,
            "p95_ms": percentile(lats, 95.0),
        }
    return summary


def _build_summary(
    outcome: SoakOutcome, rss: RssAnalysis, gates: dict[str, bool]
) -> dict[str, object]:
    total = len(outcome.samples)
    errors = sum(1 for s in outcome.samples if not s.ok)
    http_5xx = sum(1 for s in outcome.samples if 500 <= s.status_code < 600)
    error_rate = (errors / total) if total > 0 else 1.0
    lats = sorted(s.latency_ms for s in outcome.samples)
    return {
        "elapsed_s": outcome.elapsed_s,
        "total_requests": total,
        "error_count": errors,
        "http_5xx_count": http_5xx,
        "error_rate": round(error_rate, 6),
        "latency_ms": {
            "p50": percentile(lats, 50.0),
            "p95": percentile(lats, 95.0),
            "p99": percentile(lats, 99.0),
            "max": round(lats[-1], 2) if lats else 0.0,
        },
        "per_route": _summarize_routes(outcome.samples),
        "rss": {
            "sample_count": rss.sample_count,
            "warmup_mean_mb": rss.warmup_mean_mb,
            "tail_mean_mb": rss.tail_mean_mb,
            "peak_mb": rss.peak_mb,
            "drift_pct": rss.drift_pct,
            "slope_mb_per_min": rss.slope_mb_per_min,
        },
        "restart_count": outcome.restart_count,
        "passed": all(gates.values()),
    }


def _serialize_config(cfg: SoakConfig) -> dict[str, object]:
    return {
        "base_url": cfg.base_url,
        "container_name": cfg.container_name,
        "duration_s": cfg.duration,
        "concurrency": cfg.concurrency,
        "max_rss_drift_pct": cfg.max_rss_drift_pct,
        "max_error_rate": cfg.max_error_rate,
    }


def _evaluate_container_gates(
    cfg: SoakConfig, outcome: SoakOutcome, rss: RssAnalysis
) -> tuple[bool, bool]:
    if cfg.container_name is None:
        rss_ok = rss.drift_pct is None or rss.drift_pct <= cfg.max_rss_drift_pct
        restarts_ok = outcome.restart_count in (None, 0)
        return rss_ok, restarts_ok
    rss_ok = rss.drift_pct is not None and rss.drift_pct <= cfg.max_rss_drift_pct
    restarts_ok = outcome.restart_count is not None and outcome.restart_count == 0
    return rss_ok, restarts_ok


def build_scorecard(cfg: SoakConfig, outcome: SoakOutcome) -> dict[str, object]:
    """Build the honua.eval.aot-soak.v1 JSON scorecard and evaluate gates."""
    total = len(outcome.samples)
    errors = sum(1 for s in outcome.samples if not s.ok)
    error_rate = (errors / total) if total > 0 else 1.0
    rss = analyze_rss_drift(outcome.rss_samples, cfg.duration)
    rss_drift_ok, zero_restarts_ok = _evaluate_container_gates(cfg, outcome, rss)
    gates = {
        "error_rate_ok": total > 0 and error_rate <= cfg.max_error_rate,
        "rss_drift_ok": rss_drift_ok,
        "zero_restarts_ok": zero_restarts_ok,
    }
    return {
        "schema": SCHEMA_VERSION,
        "timestamp_utc": datetime.now(UTC).isoformat(),
        "config": _serialize_config(cfg),
        "summary": _build_summary(outcome, rss, gates),
        "gates": gates,
    }
