"""Unit tests for scripts/docker/test-day2-chaos-and-recovery.py."""

from __future__ import annotations

import importlib.util
import io
import json
import sys
import tempfile
import unittest
from collections import deque
from dataclasses import dataclass, field
from pathlib import Path
from unittest.mock import patch

SCRIPT_PATH = Path(__file__).resolve().with_name("test-day2-chaos-and-recovery.py")


def _load_harness():
    spec = importlib.util.spec_from_file_location("day2_chaos_recovery", SCRIPT_PATH)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Unable to load {SCRIPT_PATH}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


HARNESS = _load_harness()


class FakeClock:
    def __init__(self) -> None:
        self._now = 0.0

    def now(self) -> float:
        return self._now

    def sleep(self, seconds: float) -> None:
        self._now += seconds

    def utc_iso(self) -> str:
        return "2026-11-25T00:00:00Z"


@dataclass
class FakeCommandAdapter:
    results: dict[str, HARNESS.CommandResult] = field(default_factory=dict)
    calls: list[tuple[str, ...]] = field(default_factory=list)

    def run(self, spec) -> HARNESS.CommandResult:
        self.calls.append(spec.args)
        joined = " ".join(spec.args)
        for token, result in self.results.items():
            if token in joined:
                return result
        return HARNESS.CommandResult(0, stdout="0\n")


@dataclass
class FakeHttpAdapter:
    routes: dict[tuple[str, str], deque] = field(default_factory=dict)
    requests: list[HARNESS.HttpRequest] = field(default_factory=list)

    def send(self, request) -> HARNESS.HttpResponse:
        self.requests.append(request)
        key = (request.method, request.url)
        queue = self.routes.get(key)
        if not queue:
            return HARNESS.HttpResponse(404, "{}")
        return queue.popleft() if len(queue) > 1 else queue[0]


def _route_map(entries: dict[tuple[str, str], list[HARNESS.HttpResponse]]) -> FakeHttpAdapter:
    return FakeHttpAdapter({key: deque(values) for key, values in entries.items()})


def _make_env(
    http: FakeHttpAdapter,
    command: FakeCommandAdapter | None = None,
    config: HARNESS.ScenarioConfig | None = None,
) -> HARNESS.ChaosEnvironment:
    clock = FakeClock()
    return HARNESS.ChaosEnvironment(
        config=config or HARNESS.ScenarioConfig(poll_interval_seconds=1.0, max_wait_seconds=20.0),
        command=command or FakeCommandAdapter(),
        http=http,
        clock=HARNESS.ClockAdapter(now=clock.now, sleep=clock.sleep, utc_iso=clock.utc_iso),
    )


def _ok(payload: dict[str, object]) -> HARNESS.HttpResponse:
    return HARNESS.HttpResponse(200, json.dumps(payload))


def _all_passing_routes() -> dict[tuple[str, str], list[HARNESS.HttpResponse]]:
    base = "http://127.0.0.1:8080"
    rep = "http://127.0.0.1:8082"
    jwks = "http://127.0.0.1:8089"
    query_url = f"{base}/rest/services/day2_parcels/FeatureServer/0/query?where=1%3D1&outFields=*&f=json"
    return {
        ("GET", f"{base}/healthz/live"): [HARNESS.HttpResponse(200, "Healthy")],
        ("GET", f"{base}/healthz/ready"): [
            HARNESS.HttpResponse(503, "Not Ready"),
            HARNESS.HttpResponse(503, "Not Ready"),
            HARNESS.HttpResponse(200, "Ready"),
        ],
        ("GET", f"{rep}/healthz/ready"): [HARNESS.HttpResponse(200, "Ready")],
        ("GET", f"{base}/rest/services?f=json"): [_ok({"count": 3, "version": "v2"})],
        ("GET", f"{rep}/rest/services?f=json"): [
            _ok({"count": 3, "version": "v1"}),
            _ok({"count": 3, "version": "v2"}),
        ],
        ("GET", query_url): [_ok({"count": 12}), _ok({"count": 12}), _ok({"count": 12})],
        ("GET", f"{base}/api/v1/admin/connections"): [_ok({"decrypted": 2})],
        ("POST", f"{base}/api/v1/admin/diagnostics/temp-write"): [
            HARNESS.HttpResponse(507, "disk full"),
            HARNESS.HttpResponse(200, "ok"),
        ],
        ("POST", f"{jwks}/rotate"): [_ok({"kid": "k2"})],
        ("GET", f"{base}/api/v1/admin/status"): [
            HARNESS.HttpResponse(200, "ok"),
            HARNESS.HttpResponse(401, "unauthorized"),
            HARNESS.HttpResponse(401, "unauthorized"),
            HARNESS.HttpResponse(401, "unauthorized"),
            HARNESS.HttpResponse(401, "unauthorized"),
            HARNESS.HttpResponse(401, "unauthorized"),
        ],
        ("GET", f"{jwks}/metrics"): [_ok({"refresh_count": 1})],
        ("POST", f"{base}/api/v1/admin/metadata/refresh"): [_ok({"version": "v2"})],
    }


class Day2ChaosScenarioTests(unittest.TestCase):
    def test_cold_start_postgres_delayed_passes_within_budget(self) -> None:
        base = "http://127.0.0.1:8080"
        http = _route_map(
            {
                ("GET", f"{base}/healthz/live"): [HARNESS.HttpResponse(200, "Healthy")],
                ("GET", f"{base}/healthz/ready"): [
                    HARNESS.HttpResponse(503, "Not Ready"),
                    HARNESS.HttpResponse(503, "Not Ready"),
                    HARNESS.HttpResponse(200, "Ready"),
                ],
            }
        )
        outcome = HARNESS.scenario_cold_start_postgres_delayed(_make_env(http))
        self.assertTrue(outcome.passed)
        self.assertEqual(outcome.category, "cold-start")
        self.assertEqual(outcome.mttr_seconds, 1.0)
        self.assertFalse(outcome.data_loss)

    def test_concurrent_replicas_migration_lock_passes_with_zero_duplicates(self) -> None:
        http = _route_map(
            {
                ("GET", "http://127.0.0.1:8080/healthz/ready"): [HARNESS.HttpResponse(200, "Ready")],
                ("GET", "http://127.0.0.1:8082/healthz/ready"): [
                    HARNESS.HttpResponse(503, "Not Ready"),
                    HARNESS.HttpResponse(200, "Ready"),
                ],
            }
        )
        cmd = FakeCommandAdapter(results={"schemaversions": HARNESS.CommandResult(0, "0\n")})
        outcome = HARNESS.scenario_concurrent_replicas_migration_lock(_make_env(http, cmd))
        self.assertTrue(outcome.passed)
        self.assertEqual(outcome.mttr_seconds, 1.0)
        self.assertFalse(outcome.data_loss)

    def test_postgres_restart_mid_traffic_recovers_without_data_loss(self) -> None:
        base = "http://127.0.0.1:8080"
        http = _route_map(
            {
                ("GET", f"{base}/rest/services?f=json"): [_ok({"count": 5}), _ok({"count": 5})],
                ("GET", f"{base}/healthz/live"): [HARNESS.HttpResponse(200, "Healthy")],
                ("GET", f"{base}/healthz/ready"): [
                    HARNESS.HttpResponse(503, "Not Ready"),
                    HARNESS.HttpResponse(200, "Ready"),
                ],
            }
        )
        outcome = HARNESS.scenario_postgres_restart_mid_traffic(_make_env(http))
        self.assertTrue(outcome.passed)
        self.assertFalse(outcome.data_loss)
        self.assertEqual(outcome.mttr_seconds, 1.0)

    def test_live_ddl_cached_plan_invalidation_auto_heals(self) -> None:
        query_url = (
            "http://127.0.0.1:8080/rest/services/day2_parcels/FeatureServer/0/query"
            "?where=1%3D1&outFields=*&f=json"
        )
        http = _route_map(
            {
                ("GET", query_url): [
                    _ok({"count": 10}),
                    _ok({"count": 10}),
                    HARNESS.HttpResponse(500, "cached plan"),
                    _ok({"count": 10}),
                ]
            }
        )
        outcome = HARNESS.scenario_live_ddl_cached_plan_invalidation(_make_env(http))
        self.assertTrue(outcome.passed)
        self.assertEqual(outcome.category, "cached-plan-invalidation")
        self.assertFalse(outcome.data_loss)

    def test_backup_wipe_restore_preserves_secrets_and_layers(self) -> None:
        base = "http://127.0.0.1:8080"
        http = _route_map(
            {
                ("GET", f"{base}/rest/services?f=json"): [_ok({"count": 4}), _ok({"count": 4})],
                ("GET", f"{base}/healthz/ready"): [HARNESS.HttpResponse(200, "Ready")],
                ("GET", f"{base}/api/v1/admin/connections"): [_ok({"decrypted": 3})],
            }
        )
        outcome = HARNESS.scenario_backup_wipe_restore_secrets_and_layers(_make_env(http))
        self.assertTrue(outcome.passed)
        self.assertFalse(outcome.data_loss)

    def test_tmpfs_disk_full_returns_bounded_error_and_recovers(self) -> None:
        base = "http://127.0.0.1:8080"
        http = _route_map(
            {
                ("POST", f"{base}/api/v1/admin/diagnostics/temp-write"): [
                    HARNESS.HttpResponse(507, "no space"),
                    HARNESS.HttpResponse(200, "ok"),
                ],
                ("GET", f"{base}/healthz/live"): [HARNESS.HttpResponse(200, "Healthy")],
                ("GET", f"{base}/healthz/ready"): [HARNESS.HttpResponse(200, "Ready")],
            }
        )
        outcome = HARNESS.scenario_tmpfs_disk_full_bounded_recovery(_make_env(http))
        self.assertTrue(outcome.passed)
        self.assertFalse(outcome.data_loss)

    def test_jwks_key_rotation_auto_refreshes_and_rate_limits_bursts(self) -> None:
        base = "http://127.0.0.1:8080"
        jwks = "http://127.0.0.1:8089"
        http = _route_map(
            {
                ("POST", f"{jwks}/rotate"): [_ok({"kid": "k2"})],
                ("GET", f"{base}/api/v1/admin/status"): [
                    HARNESS.HttpResponse(401, "stale"),
                    HARNESS.HttpResponse(200, "ok"),
                    HARNESS.HttpResponse(401, "unknown"),
                    HARNESS.HttpResponse(401, "unknown"),
                    HARNESS.HttpResponse(401, "unknown"),
                    HARNESS.HttpResponse(401, "unknown"),
                    HARNESS.HttpResponse(401, "unknown"),
                ],
                ("GET", f"{jwks}/metrics"): [_ok({"refresh_count": 2})],
            }
        )
        outcome = HARNESS.scenario_jwks_key_rotation_rate_limited(_make_env(http))
        self.assertTrue(outcome.passed)
        self.assertEqual(outcome.category, "jwks-rotation")

    def test_two_replica_metadata_convergence_under_5s(self) -> None:
        http = _route_map(
            {
                ("POST", "http://127.0.0.1:8080/api/v1/admin/metadata/refresh"): [
                    _ok({"version": "v7"})
                ],
                ("GET", "http://127.0.0.1:8082/rest/services?f=json"): [
                    _ok({"version": "v6"}),
                    _ok({"version": "v6"}),
                    _ok({"version": "v7"}),
                ],
            }
        )
        outcome = HARNESS.scenario_two_replica_metadata_convergence(_make_env(http))
        self.assertTrue(outcome.passed)
        self.assertEqual(outcome.mttr_seconds, 2.0)
        self.assertEqual(outcome.mttr_budget_seconds, 5.0)


class Day2ChaosGatingAndScorecardTests(unittest.TestCase):
    def test_mttr_budget_violation_fails_scenario_and_gate(self) -> None:
        base = "http://127.0.0.1:8080"
        http = _route_map(
            {
                ("GET", f"{base}/healthz/live"): [HARNESS.HttpResponse(200, "Healthy")],
                ("GET", f"{base}/healthz/ready"): [
                    HARNESS.HttpResponse(503, "Not Ready"),
                    HARNESS.HttpResponse(503, "Not Ready"),
                    HARNESS.HttpResponse(503, "Not Ready"),
                    HARNESS.HttpResponse(503, "Not Ready"),
                    HARNESS.HttpResponse(200, "Ready"),
                ],
            }
        )
        cfg = HARNESS.ScenarioConfig(
            mttr_budget_seconds=2.0,
            poll_interval_seconds=1.0,
            max_wait_seconds=10.0,
        )
        outcome = HARNESS.scenario_cold_start_postgres_delayed(_make_env(http, config=cfg))
        self.assertFalse(outcome.within_mttr_budget)
        self.assertFalse(outcome.passed)
        scorecard = HARNESS.build_scorecard(cfg, [outcome], "2026-11-25T00:00:00Z")
        self.assertEqual(scorecard["overall_gate"], "fail")

    def test_replica_convergence_enforces_5s_ceiling(self) -> None:
        http = _route_map(
            {
                ("POST", "http://127.0.0.1:8080/api/v1/admin/metadata/refresh"): [
                    _ok({"version": "v9"})
                ],
                ("GET", "http://127.0.0.1:8082/rest/services?f=json"): [
                    _ok({"version": "v8"}),
                ],
            }
        )
        cfg = HARNESS.ScenarioConfig(mttr_budget_seconds=30.0, poll_interval_seconds=2.0)
        outcome = HARNESS.scenario_two_replica_metadata_convergence(_make_env(http, config=cfg))
        self.assertFalse(outcome.passed)
        self.assertEqual(outcome.mttr_budget_seconds, 5.0)
        self.assertTrue(outcome.data_loss)

    def test_data_loss_fails_scenario_and_sets_scorecard_flag(self) -> None:
        base = "http://127.0.0.1:8080"
        http = _route_map(
            {
                ("GET", f"{base}/rest/services?f=json"): [_ok({"count": 4}), _ok({"count": 2})],
                ("GET", f"{base}/healthz/ready"): [HARNESS.HttpResponse(200, "Ready")],
                ("GET", f"{base}/api/v1/admin/connections"): [_ok({"decrypted": 0})],
            }
        )
        outcome = HARNESS.scenario_backup_wipe_restore_secrets_and_layers(_make_env(http))
        self.assertTrue(outcome.data_loss)
        self.assertFalse(outcome.passed)
        scorecard = HARNESS.build_scorecard(
            HARNESS.ScenarioConfig(), [outcome], "2026-11-25T00:00:00Z"
        )
        self.assertTrue(scorecard["any_data_loss"])
        self.assertEqual(scorecard["overall_gate"], "fail")

    def test_cli_writes_v1_scorecard_for_all_eight_scenarios(self) -> None:
        env = _make_env(_route_map(_all_passing_routes()))
        with tempfile.TemporaryDirectory() as tmpdir:
            scorecard_path = Path(tmpdir) / "day2-scorecard.json"
            stdout = io.StringIO()
            argv = [
                "--scenario",
                "all",
                "--image",
                "ghcr.io/honua-io/honua-server:2026.1.1",
                "--mttr-budget-seconds",
                "15",
                "--scorecard",
                str(scorecard_path),
            ]
            with patch("sys.stdout", stdout):
                exit_code = HARNESS.main(argv, env=env)
            self.assertEqual(exit_code, 0)
            written = json.loads(scorecard_path.read_text(encoding="utf-8"))
            self.assertEqual(written["schema"], "honua.eval.day2-chaos.v1")
            self.assertEqual(written["overall_gate"], "pass")
            self.assertEqual(written["passed_count"], 8)
            self.assertEqual(written["failed_count"], 0)
            self.assertFalse(written["any_data_loss"])
            self.assertEqual(len(written["scenarios"]), 8)


if __name__ == "__main__":
    unittest.main()
