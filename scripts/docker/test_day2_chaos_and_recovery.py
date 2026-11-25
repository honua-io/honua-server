"""Unit tests for scripts/docker/test-day2-chaos-and-recovery.py."""

from __future__ import annotations

import base64
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
SAMPLE_SQL_DUMP = "-- PostgreSQL database dump\nCREATE TABLE day2_parcels (id int);\n"


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
    specs: list[HARNESS.CommandSpec] = field(default_factory=list)

    def run(self, spec) -> HARNESS.CommandResult:
        self.specs.append(spec)
        joined = " ".join(spec.args)
        for token, result in self.results.items():
            if token in joined:
                return result
        if "pg_dump" in joined:
            return HARNESS.CommandResult(0, stdout=SAMPLE_SQL_DUMP)
        if "ps --format {{.Image}} honua" in joined:
            image = (spec.env or {}).get("HONUA_SERVER_IMAGE", "honua-server:local")
            return HARNESS.CommandResult(0, stdout=f"{image}\n")
        return HARNESS.CommandResult(0, stdout="0\n")


@dataclass
class FakeHttpAdapter:
    routes: dict[tuple[str, str], deque] = field(default_factory=dict)
    requests: list[HARNESS.HttpRequest] = field(default_factory=list)

    def send(self, request) -> HARNESS.HttpResponse:
        self.requests.append(request)
        queue = self.routes.get((request.method, request.url))
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


def _services_dir(count: int, version: str = "v2") -> HARNESS.HttpResponse:
    services = [{"name": f"layer_{i}", "type": "FeatureServer"} for i in range(count)]
    return _ok({"currentVersion": 11.2, "folders": [], "services": services, "version": version})


def _feature_set(count: int) -> HARNESS.HttpResponse:
    features = [{"attributes": {"objectid": i + 1}} for i in range(count)]
    return _ok({"objectIdFieldName": "objectid", "features": features})


def _decode_jwt_header(bearer_value: str) -> dict[str, object]:
    token = bearer_value.removeprefix("Bearer ")
    parts = token.split(".")
    if len(parts) != 3:
        raise AssertionError(f"Expected 3-segment JWT, got {token!r}")
    padded = parts[0] + "=" * (-len(parts[0]) % 4)
    return json.loads(base64.urlsafe_b64decode(padded).decode("utf-8"))


def _all_passing_routes() -> dict[tuple[str, str], list[HARNESS.HttpResponse]]:
    base, rep, jwks = "http://127.0.0.1:8080", "http://127.0.0.1:8082", "http://127.0.0.1:8089"
    query_url = f"{base}/rest/services/day2_parcels/FeatureServer/0/query?where=1%3D1&outFields=*&f=json"
    return {
        ("GET", f"{base}/healthz/live"): [HARNESS.HttpResponse(200, "Healthy")],
        ("GET", f"{base}/healthz/ready"): [
            HARNESS.HttpResponse(503, "Not Ready"),
            HARNESS.HttpResponse(503, "Not Ready"),
            HARNESS.HttpResponse(200, "Ready"),
        ],
        ("GET", f"{rep}/healthz/ready"): [HARNESS.HttpResponse(200, "Ready")],
        ("GET", f"{base}/rest/services?f=json"): [_services_dir(3, "v2")],
        ("GET", f"{rep}/rest/services?f=json"): [_services_dir(3, "v1"), _services_dir(3, "v2")],
        ("GET", query_url): [_feature_set(12), _feature_set(12), _feature_set(12)],
        ("GET", f"{base}/api/v1/admin/connections"): [_ok({"decrypted": 2})],
        ("POST", f"{base}/api/v1/admin/diagnostics/temp-write"): [
            HARNESS.HttpResponse(507, "disk full"),
            HARNESS.HttpResponse(200, "ok"),
        ],
        ("POST", f"{jwks}/rotate"): [_ok({"kid": "k2"})],
        ("GET", f"{base}/api/v1/admin/status"): [
            HARNESS.HttpResponse(200, "ok"),
            *[HARNESS.HttpResponse(401, "unauthorized")] * 5,
        ],
        ("GET", f"{jwks}/metrics"): [_ok({"refresh_count": 1})],
        ("POST", f"{base}/api/v1/admin/metadata/refresh"): [_ok({"version": "v2"})],
    }


class Day2ChaosScenarioTests(unittest.TestCase):
    def test_cold_start_postgres_delayed_passes_within_budget(self) -> None:
        base = "http://127.0.0.1:8080"
        http = _route_map({
            ("GET", f"{base}/healthz/live"): [HARNESS.HttpResponse(200, "Healthy")],
            ("GET", f"{base}/healthz/ready"): [
                HARNESS.HttpResponse(503, "Not Ready"),
                HARNESS.HttpResponse(503, "Not Ready"),
                HARNESS.HttpResponse(200, "Ready"),
            ],
        })
        cmd = FakeCommandAdapter()
        outcome = HARNESS.scenario_cold_start_postgres_delayed(_make_env(http, cmd))
        self.assertTrue(outcome.passed)
        self.assertEqual(outcome.mttr_seconds, 1.0)
        self.assertTrue(all((s.env or {}).get("HONUA_SERVER_IMAGE") == "honua-server:local" for s in cmd.specs))

    def test_concurrent_replicas_migration_lock_passes_with_zero_duplicates(self) -> None:
        http = _route_map({
            ("GET", "http://127.0.0.1:8080/healthz/ready"): [HARNESS.HttpResponse(200, "Ready")],
            ("GET", "http://127.0.0.1:8082/healthz/ready"): [
                HARNESS.HttpResponse(503, "Not Ready"), HARNESS.HttpResponse(200, "Ready"),
            ],
        })
        cmd = FakeCommandAdapter(results={"schemaversions": HARNESS.CommandResult(0, "0\n")})
        outcome = HARNESS.scenario_concurrent_replicas_migration_lock(_make_env(http, cmd))
        self.assertTrue(outcome.passed)
        self.assertEqual(outcome.mttr_seconds, 1.0)
        self.assertFalse(outcome.data_loss)

    def test_postgres_restart_mid_traffic_reads_geoservices_directory_shape(self) -> None:
        base = "http://127.0.0.1:8080"
        http = _route_map({
            ("GET", f"{base}/rest/services?f=json"): [_services_dir(5), _services_dir(5)],
            ("GET", f"{base}/healthz/live"): [HARNESS.HttpResponse(200, "Healthy")],
            ("GET", f"{base}/healthz/ready"): [HARNESS.HttpResponse(503), HARNESS.HttpResponse(200)],
        })
        outcome = HARNESS.scenario_postgres_restart_mid_traffic(_make_env(http))
        self.assertTrue(outcome.passed)
        self.assertFalse(outcome.data_loss)

    def test_live_ddl_cached_plan_invalidation_reads_features_array(self) -> None:
        query_url = "http://127.0.0.1:8080/rest/services/day2_parcels/FeatureServer/0/query?where=1%3D1&outFields=*&f=json"
        http = _route_map({
            ("GET", query_url): [
                _feature_set(10), _feature_set(10), HARNESS.HttpResponse(500), _feature_set(10),
            ],
        })
        outcome = HARNESS.scenario_live_ddl_cached_plan_invalidation(_make_env(http))
        self.assertTrue(outcome.passed)
        self.assertFalse(outcome.data_loss)

    def test_backup_wipe_restore_pipes_pg_dump_into_psql_stdin(self) -> None:
        base = "http://127.0.0.1:8080"
        http = _route_map({
            ("GET", f"{base}/rest/services?f=json"): [_services_dir(4), _services_dir(4)],
            ("GET", f"{base}/healthz/ready"): [HARNESS.HttpResponse(200, "Ready")],
            ("GET", f"{base}/api/v1/admin/connections"): [_ok({"decrypted": 3})],
        })
        cmd = FakeCommandAdapter()
        outcome = HARNESS.scenario_backup_wipe_restore_secrets_and_layers(_make_env(http, cmd))
        self.assertTrue(outcome.passed)
        restore_specs = [s for s in cmd.specs if "ON_ERROR_STOP=1" in s.args]
        self.assertEqual(len(restore_specs), 1)
        self.assertEqual(restore_specs[0].stdin_text, SAMPLE_SQL_DUMP)

    def test_backup_failure_aborts_before_wiping_schema(self) -> None:
        base = "http://127.0.0.1:8080"
        http = _route_map({("GET", f"{base}/rest/services?f=json"): [_services_dir(4)]})
        cmd = FakeCommandAdapter(results={"pg_dump": HARNESS.CommandResult(1, stdout="", stderr="err")})
        outcome = HARNESS.scenario_backup_wipe_restore_secrets_and_layers(_make_env(http, cmd))
        self.assertFalse(outcome.passed)
        self.assertFalse(outcome.data_loss)
        self.assertTrue(outcome.checks["aborted_before_wipe"])
        self.assertFalse(any("DROP SCHEMA" in " ".join(s.args) for s in cmd.specs))

    def test_tmpfs_disk_full_returns_bounded_error_and_recovers(self) -> None:
        base = "http://127.0.0.1:8080"
        http = _route_map({
            ("POST", f"{base}/api/v1/admin/diagnostics/temp-write"): [
                HARNESS.HttpResponse(507, "no space"), HARNESS.HttpResponse(200, "ok"),
            ],
            ("GET", f"{base}/healthz/live"): [HARNESS.HttpResponse(200, "Healthy")],
            ("GET", f"{base}/healthz/ready"): [HARNESS.HttpResponse(200, "Ready")],
        })
        outcome = HARNESS.scenario_tmpfs_disk_full_bounded_recovery(_make_env(http))
        self.assertTrue(outcome.passed)

    def test_jwks_key_rotation_sends_structured_jwt_headers_and_rate_limits(self) -> None:
        base, jwks = "http://127.0.0.1:8080", "http://127.0.0.1:8089"
        http = _route_map({
            ("POST", f"{jwks}/rotate"): [_ok({"kid": "k2"})],
            ("GET", f"{base}/api/v1/admin/status"): [
                HARNESS.HttpResponse(401), HARNESS.HttpResponse(200),
                *[HARNESS.HttpResponse(401)] * 5,
            ],
            ("GET", f"{jwks}/metrics"): [_ok({"refresh_count": 2})],
        })
        outcome = HARNESS.scenario_jwks_key_rotation_rate_limited(_make_env(http))
        self.assertTrue(outcome.passed)
        auth_headers = [r.headers["Authorization"] for r in http.requests if "Authorization" in r.headers]
        decoded_kids = [_decode_jwt_header(value)["kid"] for value in auth_headers]
        self.assertEqual(decoded_kids[:2], ["k2", "k2"])
        self.assertEqual(decoded_kids[2:], [f"unknown-kid-{i}" for i in range(5)])

    def test_two_replica_metadata_convergence_under_5s(self) -> None:
        http = _route_map({
            ("POST", "http://127.0.0.1:8080/api/v1/admin/metadata/refresh"): [_ok({"version": "v7"})],
            ("GET", "http://127.0.0.1:8082/rest/services?f=json"): [
                _services_dir(2, "v6"), _services_dir(2, "v6"), _services_dir(2, "v7"),
            ],
        })
        outcome = HARNESS.scenario_two_replica_metadata_convergence(_make_env(http))
        self.assertTrue(outcome.passed)
        self.assertEqual(outcome.mttr_seconds, 2.0)


class Day2ChaosGatingAndScorecardTests(unittest.TestCase):
    def test_mttr_budget_violation_fails_scenario_and_gate(self) -> None:
        base = "http://127.0.0.1:8080"
        http = _route_map({
            ("GET", f"{base}/healthz/live"): [HARNESS.HttpResponse(200, "Healthy")],
            ("GET", f"{base}/healthz/ready"): [
                *[HARNESS.HttpResponse(503)] * 4, HARNESS.HttpResponse(200),
            ],
        })
        cfg = HARNESS.ScenarioConfig(mttr_budget_seconds=2.0, poll_interval_seconds=1.0, max_wait_seconds=10.0)
        outcome = HARNESS.scenario_cold_start_postgres_delayed(_make_env(http, config=cfg))
        self.assertFalse(outcome.within_mttr_budget)
        card = HARNESS.build_scorecard(HARNESS.ScorecardSpec(cfg, [outcome], "2026-11-25T00:00:00Z"))
        self.assertEqual(card["overall_gate"], "fail")

    def test_data_loss_fails_scenario_and_sets_scorecard_flag(self) -> None:
        base = "http://127.0.0.1:8080"
        http = _route_map({
            ("GET", f"{base}/rest/services?f=json"): [_services_dir(4), _services_dir(2)],
            ("GET", f"{base}/healthz/ready"): [HARNESS.HttpResponse(200, "Ready")],
            ("GET", f"{base}/api/v1/admin/connections"): [_ok({"decrypted": 0})],
        })
        outcome = HARNESS.scenario_backup_wipe_restore_secrets_and_layers(_make_env(http))
        self.assertTrue(outcome.data_loss)
        card = HARNESS.build_scorecard(
            HARNESS.ScorecardSpec(HARNESS.ScenarioConfig(), [outcome], "2026-11-25T00:00:00Z"),
        )
        self.assertTrue(card["any_data_loss"])
        self.assertEqual(card["overall_gate"], "fail")

    def test_image_mismatch_fails_cli_gate_and_marks_image_unverified(self) -> None:
        cfg = HARNESS.ScenarioConfig(image="ghcr.io/honua-io/honua-server:2026.1.1", poll_interval_seconds=1.0)
        cmd = FakeCommandAdapter(results={"ps --format {{.Image}} honua": HARNESS.CommandResult(0, "honua-server:local\n")})
        env = _make_env(_route_map(_all_passing_routes()), cmd, cfg)
        stdout = io.StringIO()
        with patch("sys.stdout", stdout):
            exit_code = HARNESS.main(["--scenario", "all", "--image", cfg.image], env=env)
        self.assertEqual(exit_code, 1)
        payload = json.loads(stdout.getvalue())
        self.assertFalse(payload["image_verified"])
        self.assertEqual(payload["overall_gate"], "fail")

    def test_cli_writes_v1_scorecard_and_verifies_image_for_all_eight_scenarios(self) -> None:
        image = "ghcr.io/honua-io/honua-server:2026.1.1"
        cfg = HARNESS.ScenarioConfig(image=image, poll_interval_seconds=1.0, max_wait_seconds=20.0)
        cmd = FakeCommandAdapter()
        env = _make_env(_route_map(_all_passing_routes()), cmd, cfg)
        with tempfile.TemporaryDirectory() as tmpdir:
            scorecard_path = Path(tmpdir) / "day2-scorecard.json"
            argv = ["--scenario", "all", "--image", image, "--mttr-budget-seconds", "15", "--scorecard", str(scorecard_path)]
            with patch("sys.stdout", io.StringIO()):
                exit_code = HARNESS.main(argv, env=env)
            self.assertEqual(exit_code, 0)
            written = json.loads(scorecard_path.read_text(encoding="utf-8"))
            self.assertEqual(written["schema"], "honua.eval.day2-chaos.v1")
            self.assertEqual(written["overall_gate"], "pass")
            self.assertTrue(written["image_verified"])
            self.assertEqual(written["passed_count"], 8)
            self.assertTrue(all((s.env or {}).get("HONUA_SERVER_IMAGE") == image for s in cmd.specs))


if __name__ == "__main__":
    unittest.main()
