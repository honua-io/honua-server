#!/usr/bin/env python3
"""Aggregate paired Arm64 GC benchmark artifacts produced by arm64-gc-benchmark.yml.

Computes medians and min-max ranges across >= 5 paired runs on GitHub-hosted
ubuntu-24.04-arm runners, plus paired deltas against the shipped `defaults`
profile on the same runner, and writes JSON + Markdown reports.
"""

from __future__ import annotations

import argparse
import json
import math
import statistics
import sys
from pathlib import Path
from typing import Any

BAR_P95_MS = 500.0
BYTES_PER_MIB = 1024 * 1024
MIN_PAIRED_RUNS = 5


def _median_and_range(values: list[float], digits: int = 1) -> tuple[float | None, list[float] | None]:
    finite = [v for v in values if isinstance(v, (int, float)) and math.isfinite(v)]
    if not finite:
        return None, None
    return round(statistics.median(finite), digits), [round(min(finite), digits), round(max(finite), digits)]


def _read_json(path: Path) -> dict[str, Any] | None:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else None
    except (OSError, ValueError):
        return None


def _load_pairs(input_dir: Path) -> list[dict[str, Any]]:
    pairs: list[dict[str, Any]] = []
    for meta_path in sorted(input_dir.rglob("runner-meta.json")):
        meta = _read_json(meta_path)
        if not meta:
            continue
        profiles: dict[str, dict[str, Any]] = {}
        diag_dir = meta_path.parent / "gc-diagnostic"
        if diag_dir.is_dir():
            for res_path in sorted(diag_dir.glob("*/result.json")):
                res = _read_json(res_path)
                if res and res.get("gc_profile"):
                    profiles[str(res["gc_profile"])] = res
        pairs.append({"meta": meta, "profiles": profiles})
    pairs.sort(key=lambda item: int(item["meta"].get("pair", 0)))
    return pairs


def _size_entry(pair: dict[str, Any], size: str, profile: str) -> dict[str, Any]:
    res = pair["profiles"].get(profile) or {}
    return (res.get("sizes") or {}).get(size) or {}


def _level_row(pair: dict[str, Any], key: tuple[str, str, str]) -> dict[str, Any]:
    size, profile, lvl = key
    entry = _size_entry(pair, size, profile)
    return ((entry.get("summary") or {}).get("levels") or {}).get(lvl) or {}


def _is_clean_row(entry: dict[str, Any], row: dict[str, Any]) -> bool:
    return bool(
        entry.get("exit") == 0
        and row.get("complete")
        and row.get("error_rate_max") == 0.0
        and row.get("http_failed_rate_max") == 0.0
    )


def _summarize_level(pairs: list[dict[str, Any]], key: tuple[str, str, str], min_pairs: int) -> dict[str, Any]:
    size, profile, _ = key
    all_rows = [(_size_entry(p, size, profile), _level_row(p, key)) for p in pairs]
    clean_rows = [r for e, r in all_rows if _is_clean_row(e, r)]
    p95_med, p95_rng = _median_and_range([float(r["p95_ms_median"]) for r in clean_rows if r.get("p95_ms_median") is not None])
    p99_med, p99_rng = _median_and_range([float(r["p99_ms_median"]) for r in clean_rows if r.get("p99_ms_median") is not None])
    rps_med, rps_rng = _median_and_range([float(r["rps_median"]) for r in clean_rows if r.get("rps_median") is not None])
    err_vals = [float(r["error_rate_max"]) for _, r in all_rows if r.get("error_rate_max") is not None]
    fail_vals = [float(r["http_failed_rate_max"]) for _, r in all_rows if r.get("http_failed_rate_max") is not None]
    max_err = max(err_vals) if err_vals else None
    max_http_fail = max(fail_vals) if fail_vals else None
    meets_latency = bool(
        len(clean_rows) >= min_pairs
        and p95_med is not None
        and p95_med < BAR_P95_MS
        and max_err == 0.0
        and max_http_fail == 0.0
    )
    return {
        "complete_pairs": len(clean_rows),
        "p95_ms_median": p95_med,
        "p95_ms_range": p95_rng,
        "p99_ms_median": p99_med,
        "p99_ms_range": p99_rng,
        "rps_median": rps_med,
        "rps_range": rps_rng,
        "error_rate_max": max_err,
        "http_failed_rate_max": max_http_fail,
        "meets_bar_on_median": meets_latency,
    }


def _summarize_runtime(pairs: list[dict[str, Any]], size: str, profile: str) -> dict[str, Any]:
    entries = [_size_entry(p, size, profile) for p in pairs]
    all_rts = [e.get("runtime_samples") for e in entries if isinstance(e.get("runtime_samples"), dict)]
    clean_rts = [
        e["runtime_samples"]
        for e in entries
        if e.get("exit") == 0 and isinstance(e.get("runtime_samples"), dict) and e["runtime_samples"].get("memory_evidence_complete")
    ]
    rss_med, rss_rng = _median_and_range(
        [float(rt["sampled_peak_process_rss_bytes"]) / BYTES_PER_MIB for rt in clean_rts if rt.get("sampled_peak_process_rss_bytes") is not None]
    )
    cg_med, cg_rng = _median_and_range(
        [float(rt["cgroup_peak_bytes"]) / BYTES_PER_MIB for rt in clean_rts if rt.get("cgroup_peak_bytes") is not None]
    )
    pause_med, pause_rng = _median_and_range(
        [float(rt["gc_pause_seconds_observed_delta"]) for rt in clean_rts if rt.get("gc_pause_seconds_observed_delta") is not None],
        4,
    )
    load_med, load_rng = _median_and_range(
        [float(rt["max_load1"]) for rt in clean_rts if rt.get("max_load1") is not None],
        2,
    )
    oom_vals = [bool(rt["oom_observed"]) for rt in all_rts if "oom_observed" in rt]
    restarts = [int(rt["max_restarts"]) for rt in all_rts if rt.get("max_restarts") is not None]
    return {
        "collected_pairs": len(clean_rts),
        "sampled_peak_rss_mib_median": rss_med,
        "sampled_peak_rss_mib_range": rss_rng,
        "cgroup_peak_mib_median": cg_med,
        "cgroup_peak_mib_range": cg_rng,
        "gc_pause_seconds_median": pause_med,
        "gc_pause_seconds_range": pause_rng,
        "max_load1_median": load_med,
        "max_load1_range": load_rng,
        "oom_observed": any(oom_vals) if oom_vals else None,
        "max_restarts": max(restarts) if restarts else None,
    }


def _summarize_profile(pairs: list[dict[str, Any]], scope: tuple[str, str], min_pairs: int) -> dict[str, Any]:
    size, profile = scope
    level_keys: set[str] = set()
    for pair in pairs:
        entry = _size_entry(pair, size, profile)
        level_keys.update(((entry.get("summary") or {}).get("levels") or {}).keys())
    row = _summarize_runtime(pairs, size, profile)
    runtime_healthy = row["oom_observed"] is False and row["max_restarts"] == 0
    levels = {}
    for lvl in sorted(level_keys, key=int):
        lvl_row = _summarize_level(pairs, (size, profile, lvl), min_pairs)
        lvl_row["meets_bar_on_median"] = bool(lvl_row["meets_bar_on_median"] and runtime_healthy)
        levels[lvl] = lvl_row
    row["levels"] = levels
    return row


def _paired_level_delta(pairs: list[dict[str, Any]], key: tuple[str, str, str]) -> dict[str, Any]:
    size, alt, lvl = key
    p95_deltas: list[float] = []
    rps_deltas: list[float] = []
    for pair in pairs:
        base_entry, alt_entry = _size_entry(pair, size, "defaults"), _size_entry(pair, size, alt)
        base_row, alt_row = _level_row(pair, (size, "defaults", lvl)), _level_row(pair, (size, alt, lvl))
        if not (_is_clean_row(base_entry, base_row) and _is_clean_row(alt_entry, alt_row)):
            continue
        bp95, ap95 = base_row.get("p95_ms_median"), alt_row.get("p95_ms_median")
        brps, arps = base_row.get("rps_median"), alt_row.get("rps_median")
        if bp95 and ap95 is not None and bp95 > 0:
            p95_deltas.append(((ap95 - bp95) / bp95) * 100.0)
        if brps and arps is not None and brps > 0:
            rps_deltas.append(((arps - brps) / brps) * 100.0)
    p95_med, p95_rng = _median_and_range(p95_deltas)
    rps_med, rps_rng = _median_and_range(rps_deltas)
    return {
        "paired_count": len(p95_deltas),
        "p95_pct_delta_median": p95_med,
        "p95_pct_delta_range": p95_rng,
        "rps_pct_delta_median": rps_med,
        "rps_pct_delta_range": rps_rng,
    }


def _compute_paired_deltas(pairs: list[dict[str, Any]], size: str, profiles: list[str]) -> dict[str, Any]:
    if "defaults" not in profiles:
        return {}
    base_levels: set[str] = set()
    for pair in pairs:
        entry = _size_entry(pair, size, "defaults")
        base_levels.update(((entry.get("summary") or {}).get("levels") or {}).keys())
    sorted_levels = sorted(base_levels, key=int)
    return {
        f"{alt}_vs_defaults": {lvl: _paired_level_delta(pairs, (size, alt, lvl)) for lvl in sorted_levels}
        for alt in profiles
        if alt != "defaults"
    }


def _collect_meta(pairs: list[dict[str, Any]]) -> dict[str, Any]:
    image_ids = sorted({p["meta"].get("image_id") for p in pairs if p["meta"].get("image_id")})
    revisions = sorted({p["meta"].get("source_revision") for p in pairs if p["meta"].get("source_revision")})
    archs = sorted({p["meta"].get("image_arch") for p in pairs if p["meta"].get("image_arch")})
    geobench_refs = sorted({p["meta"].get("geobench_ref") for p in pairs if p["meta"].get("geobench_ref")})
    consistent = len(image_ids) == 1 and len(revisions) == 1 and archs == ["arm64"] and len(geobench_refs) == 1
    return {
        "consistent_inputs": consistent,
        "image_ids": image_ids,
        "source_revisions": revisions,
        "architectures": archs,
        "geobench_refs": geobench_refs,
    }


def _parse_csv(raw: str | None) -> list[str]:
    if not raw:
        return []
    return [item.strip() for item in raw.split(",") if item.strip()]


def aggregate(input_dir: Path, min_pairs: int, expected: dict[str, list[str]] | None = None) -> dict[str, Any]:
    pairs = _load_pairs(input_dir)
    meta = _collect_meta(pairs)
    found_sizes = {s for p in pairs for r in p["profiles"].values() for s in (r.get("sizes") or {}).keys()}
    found_profiles = {prof for p in pairs for prof in p["profiles"].keys()}
    exp = expected or {}
    sizes = sorted(found_sizes | set(exp.get("sizes") or []))
    profiles = sorted(found_profiles | set(exp.get("profiles") or []))

    summary_sizes = {
        size: {
            "profiles": {prof: _summarize_profile(pairs, (size, prof), min_pairs) for prof in profiles},
            "paired_deltas": _compute_paired_deltas(pairs, size, profiles),
        }
        for size in sizes
    }
    enough = bool(sizes) and bool(profiles) and all(
        summary_sizes[s]["profiles"][p]["collected_pairs"] >= min_pairs for s in sizes for p in profiles
    )
    return {
        "kind": "github-hosted-arm64-paired-diagnostic",
        "runner": "ubuntu-24.04-arm",
        "noise_caveat": (
            "GitHub-hosted ubuntu-24.04-arm runners are shared and noisy; values report "
            f"the median (and min-max range) across paired runs (min_pairs={min_pairs}). "
            "Not quiet-host AWS Fargate/RDS qualification."
        ),
        "pairs_found": len(pairs),
        "min_pairs_required": min_pairs,
        **meta,
        "verdict": "COLLECTED" if (meta["consistent_inputs"] and enough) else "INCOMPLETE",
        "sizes": summary_sizes,
    }


def _fmt_med_range(med: float | None, rng: list[float] | None) -> str:
    if med is None or not rng:
        return "n/a"
    return f"{med} ({rng[0]}-{rng[1]})"


def _fmt_delta_range(med: float | None, rng: list[float] | None) -> str:
    if med is None or not rng:
        return "n/a"
    return f"{med:+.1f}% ({rng[0]:+.1f}%..{rng[1]:+.1f}%)"


def _render_size_section(size: str, size_data: dict[str, Any]) -> list[str]:
    lines = [
        f"## Size: {size}",
        "",
        "| Profile | Pairs | VU | p95 median (range) ms | p99 median (range) ms | RPS median (range) | Max err | RSS med MiB | Cgroup peak med MiB | GC pause med s | OOM / Restarts |",
        "|---|---:|---:|---|---|---|---:|---:|---:|---:|---|",
    ]
    for profile, pdata in size_data["profiles"].items():
        rss = pdata["sampled_peak_rss_mib_median"]
        cg = pdata["cgroup_peak_mib_median"]
        pause = pdata["gc_pause_seconds_median"]
        oom_str = "n/a" if pdata["oom_observed"] is None else str(pdata["oom_observed"])
        rst_str = "n/a" if pdata["max_restarts"] is None else str(pdata["max_restarts"])
        oom_rst = f"{oom_str} / {rst_str}"
        for lvl, row in pdata["levels"].items():
            p95 = _fmt_med_range(row["p95_ms_median"], row["p95_ms_range"])
            p99 = _fmt_med_range(row["p99_ms_median"], row["p99_ms_range"])
            rps = _fmt_med_range(row["rps_median"], row["rps_range"])
            err = f"{row['error_rate_max']}" if row["error_rate_max"] is not None else "n/a"
            lines.append(
                f"| `{profile}` | {row['complete_pairs']} | {lvl} | {p95} | {p99} | {rps} | {err} | {rss if rss is not None else 'n/a'} | {cg if cg is not None else 'n/a'} | {pause if pause is not None else 'n/a'} | {oom_rst} |"
            )
    lines.append("")
    return lines


def _render_delta_section(size: str, deltas: dict[str, Any]) -> list[str]:
    if not deltas:
        return []
    lines = [
        f"### Paired deltas vs `defaults` ({size})",
        "",
        "| Comparison | VU | Pairs | p95 delta median (range) % | RPS delta median (range) % |",
        "|---|---:|---:|---|---|",
    ]
    for comp, comp_levels in deltas.items():
        for lvl, drow in comp_levels.items():
            p95d = _fmt_delta_range(drow["p95_pct_delta_median"], drow["p95_pct_delta_range"])
            rpsd = _fmt_delta_range(drow["rps_pct_delta_median"], drow["rps_pct_delta_range"])
            lines.append(f"| `{comp}` | {lvl} | {drow['paired_count']} | {p95d} | {rpsd} |")
    lines.append("")
    return lines


def render_markdown(report: dict[str, Any]) -> str:
    lines: list[str] = [
        "# Arm64 GC Benchmark Paired Summary",
        "",
        f"- **Verdict:** `{report['verdict']}` (`pairs_found={report['pairs_found']}`, `min_pairs={report['min_pairs_required']}`)",
        f"- **Runner:** `{report['runner']}`",
        f"- **Image ID(s):** `{', '.join(report['image_ids']) or 'none'}`",
        f"- **Source revision(s):** `{', '.join(report['source_revisions']) or 'none'}`",
        f"- **Architecture(s):** `{', '.join(report['architectures']) or 'none'}`",
        f"- **GeoBench ref(s):** `{', '.join(report['geobench_refs']) or 'none'}`",
        "",
        f"> **Hosted-runner noise caveat:** {report['noise_caveat']}",
        "",
    ]
    for size, size_data in report["sizes"].items():
        lines.extend(_render_size_section(size, size_data))
        lines.extend(_render_delta_section(size, size_data["paired_deltas"]))
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description="Summarize paired Arm64 GC benchmark artifacts.")
    parser.add_argument("--input-dir", required=True, type=Path)
    parser.add_argument("--min-pairs", type=int, default=MIN_PAIRED_RUNS)
    parser.add_argument("--expected-sizes", default="")
    parser.add_argument("--expected-profiles", default="")
    parser.add_argument("--json-out", required=True, type=Path)
    parser.add_argument("--md-out", required=True, type=Path)
    args = parser.parse_args()

    if args.min_pairs < MIN_PAIRED_RUNS:
        parser.error(f"--min-pairs must be >= {MIN_PAIRED_RUNS}")

    expected = {"sizes": _parse_csv(args.expected_sizes), "profiles": _parse_csv(args.expected_profiles)}
    report = aggregate(args.input_dir, args.min_pairs, expected)
    args.json_out.parent.mkdir(parents=True, exist_ok=True)
    args.md_out.parent.mkdir(parents=True, exist_ok=True)
    args.json_out.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    args.md_out.write_text(render_markdown(report), encoding="utf-8")
    print(json.dumps({"verdict": report["verdict"], "pairs_found": report["pairs_found"]}))
    return 0 if report["verdict"] == "COLLECTED" else 1


if __name__ == "__main__":
    sys.exit(main())
