#!/usr/bin/env python3
"""Unit tests for summarize-arm64-gc-benchmark.py."""

from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

SCRIPT_PATH = Path(__file__).with_name("summarize-arm64-gc-benchmark.py")
SPEC = importlib.util.spec_from_file_location("summarize_arm64_gc_benchmark", SCRIPT_PATH)
assert SPEC is not None and SPEC.loader is not None
MOD = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MOD)


def _make_profile_result(profile: str, p95: float, opts: dict | None = None) -> dict:
    o = opts or {}
    return {
        "gc_profile": profile,
        "sizes": {
            "Small": {
                "exit": o.get("exit", 0),
                "runtime_samples": {
                    "memory_evidence_complete": o.get("memory_complete", True),
                    "sampled_peak_process_rss_bytes": o.get("rss_bytes", 128 * 1024 * 1024),
                    "cgroup_peak_bytes": 256 * 1024 * 1024,
                    "gc_pause_seconds_observed_delta": 0.05,
                    "max_load1": 2.5,
                    "oom_observed": o.get("oom", False),
                    "max_restarts": o.get("restarts", 0),
                },
                "summary": {
                    "levels": {
                        "10": {
                            "complete": o.get("complete", True),
                            "p95_ms_median": p95,
                            "p99_ms_median": p95 * 1.2,
                            "rps_median": o.get("rps", 200.0),
                            "error_rate_max": o.get("error_rate", 0.0),
                            "http_failed_rate_max": o.get("http_failed", 0.0),
                        }
                    }
                },
            }
        },
    }


def _write_pair(root: Path, pair: int, overrides: dict | None = None) -> None:
    cfg = {
        "image_id": "sha256:" + "a" * 64,
        "rev": "b" * 40,
        "arch": "arm64",
        "p95_defaults": 100.0,
        "p95_fixed": 90.0,
        "defaults_opts": {"rps": 200.0},
        "fixed_opts": {"rps": 220.0},
    }
    if overrides:
        cfg.update(overrides)
    pair_dir = root / f"arm64-gc-pair-{pair}"
    pair_dir.mkdir(parents=True, exist_ok=True)
    meta = {
        "pair": pair,
        "image_id": cfg["image_id"],
        "source_revision": cfg["rev"],
        "image_arch": cfg["arch"],
        "geobench_ref": "a0ec832cbe9740e623453f25e3404338cbe8c707",
    }
    (pair_dir / "runner-meta.json").write_text(json.dumps(meta), encoding="utf-8")
    specs = (("defaults", cfg["p95_defaults"], cfg["defaults_opts"]), ("fixed", cfg["p95_fixed"], cfg["fixed_opts"]))
    for profile, p95, opts in specs:
        res_dir = pair_dir / "gc-diagnostic" / f"20261121T00000{pair}Z-{profile}"
        res_dir.mkdir(parents=True, exist_ok=True)
        (res_dir / "result.json").write_text(json.dumps(_make_profile_result(profile, p95, opts)), encoding="utf-8")


class SummarizeArm64GcBenchmarkTests(unittest.TestCase):
    def test_five_paired_runs_collect_medians_and_paired_deltas(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            samples = [(100.0, 90.0), (110.0, 99.0), (120.0, 108.0), (130.0, 117.0), (140.0, 126.0)]
            for idx, (d_p95, f_p95) in enumerate(samples, start=1):
                _write_pair(root, idx, {"p95_defaults": d_p95, "p95_fixed": f_p95})

            report = MOD.aggregate(root, 5, {"sizes": ["Small"], "profiles": ["defaults", "fixed"]})
            self.assertEqual(report["verdict"], "COLLECTED")
            small_def = report["sizes"]["Small"]["profiles"]["defaults"]["levels"]["10"]
            self.assertEqual(small_def["p95_ms_median"], 120.0)
            self.assertEqual(small_def["p95_ms_range"], [100.0, 140.0])
            self.assertTrue(small_def["meets_bar_on_median"])

            delta = report["sizes"]["Small"]["paired_deltas"]["fixed_vs_defaults"]["10"]
            self.assertEqual(delta["paired_count"], 5)
            self.assertEqual(delta["p95_pct_delta_median"], -10.0)
            self.assertEqual(delta["rps_pct_delta_median"], 10.0)
            self.assertIn("shared and noisy", MOD.render_markdown(report))

    def test_missing_expected_profile_is_incomplete(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for idx in range(1, 6):
                _write_pair(root, idx)
            report = MOD.aggregate(root, 5, {"sizes": ["Small"], "profiles": ["defaults", "fixed", "heap60"]})
            self.assertEqual(report["verdict"], "INCOMPLETE")
            self.assertIsNone(report["sizes"]["Small"]["profiles"]["heap60"]["oom_observed"])
            self.assertIsNone(report["sizes"]["Small"]["profiles"]["heap60"]["max_restarts"])

    def test_erroring_and_oom_runs_excluded_from_deltas_and_bar(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for idx in range(1, 5):
                _write_pair(root, idx)
            _write_pair(
                root,
                5,
                {
                    "p95_fixed": 5.0,
                    "fixed_opts": {"rps": 500.0, "error_rate": 0.5, "oom": True, "restarts": 1},
                },
            )
            report = MOD.aggregate(root, 5, {"sizes": ["Small"], "profiles": ["defaults", "fixed"]})
            fixed = report["sizes"]["Small"]["profiles"]["fixed"]
            self.assertTrue(fixed["oom_observed"])
            self.assertEqual(fixed["max_restarts"], 1)
            self.assertFalse(fixed["levels"]["10"]["meets_bar_on_median"])
            self.assertEqual(fixed["levels"]["10"]["complete_pairs"], 4)
            delta = report["sizes"]["Small"]["paired_deltas"]["fixed_vs_defaults"]["10"]
            self.assertEqual(delta["paired_count"], 4)
            self.assertEqual(delta["p95_pct_delta_median"], -10.0)

    def test_mismatched_image_id_is_incomplete(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for idx in range(1, 5):
                _write_pair(root, idx)
            _write_pair(root, 5, {"image_id": "sha256:" + "f" * 64})
            report = MOD.aggregate(root, 5)
            self.assertFalse(report["consistent_inputs"])
            self.assertEqual(report["verdict"], "INCOMPLETE")


    def test_nonzero_exit_outlier_rss_excluded_from_memory_median(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for idx in range(1, 5):
                _write_pair(root, idx, {"fixed_opts": {"rps": 220.0, "rss_bytes": 100 * 1024 * 1024}})
            _write_pair(
                root,
                5,
                {"fixed_opts": {"exit": 1, "rps": 220.0, "rss_bytes": 999 * 1024 * 1024}},
            )
            report = MOD.aggregate(root, 5, {"sizes": ["Small"], "profiles": ["defaults", "fixed"]})
            fixed = report["sizes"]["Small"]["profiles"]["fixed"]
            self.assertEqual(fixed["collected_pairs"], 4)
            self.assertEqual(fixed["sampled_peak_rss_mib_median"], 100.0)
            self.assertEqual(report["verdict"], "INCOMPLETE")


if __name__ == "__main__":
    unittest.main()
