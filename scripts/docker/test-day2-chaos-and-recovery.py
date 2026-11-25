#!/usr/bin/env python3
"""Day-2 chaos, upgrade, and self-healing verification CLI for Honua Server."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import urllib.error
import urllib.request
from dataclasses import asdict, dataclass
from pathlib import Path

_SCRIPT_DIR = str(Path(__file__).resolve().parent)
if _SCRIPT_DIR not in sys.path:
    sys.path.insert(0, _SCRIPT_DIR)

from day2_chaos_scenarios import (
    SCENARIOS,
    ChaosEnvironment,
    ClockAdapter,
    CommandResult,
    CommandSpec,
    HttpRequest,
    HttpResponse,
    ScenarioConfig,
    ScenarioOutcome,
    build_probe_jwt,
    scenario_backup_wipe_restore_secrets_and_layers,
    scenario_cold_start_postgres_delayed,
    scenario_concurrent_replicas_migration_lock,
    scenario_jwks_key_rotation_rate_limited,
    scenario_live_ddl_cached_plan_invalidation,
    scenario_postgres_restart_mid_traffic,
    scenario_tmpfs_disk_full_bounded_recovery,
    scenario_two_replica_metadata_convergence,
    verify_running_image,
)

SCORECARD_SCHEMA = "honua.eval.day2-chaos.v1"

__all__ = [
    "SCENARIOS",
    "SCORECARD_SCHEMA",
    "ChaosEnvironment",
    "ClockAdapter",
    "CommandResult",
    "CommandSpec",
    "HttpRequest",
    "HttpResponse",
    "ScenarioConfig",
    "ScenarioOutcome",
    "ScorecardSpec",
    "SubprocessCommandAdapter",
    "UrllibHttpAdapter",
    "build_probe_jwt",
    "build_scorecard",
    "main",
    "parse_args",
    "run_suite",
    "scenario_backup_wipe_restore_secrets_and_layers",
    "scenario_cold_start_postgres_delayed",
    "scenario_concurrent_replicas_migration_lock",
    "scenario_jwks_key_rotation_rate_limited",
    "scenario_live_ddl_cached_plan_invalidation",
    "scenario_postgres_restart_mid_traffic",
    "scenario_tmpfs_disk_full_bounded_recovery",
    "scenario_two_replica_metadata_convergence",
    "verify_running_image",
]


@dataclass(frozen=True)
class ScorecardSpec:
    config: ScenarioConfig
    outcomes: list[ScenarioOutcome]
    generated_at_utc: str
    image_verified: bool = True


class SubprocessCommandAdapter:
    def run(self, spec: CommandSpec) -> CommandResult:
        merged_env = {**os.environ, **spec.env} if spec.env else None
        res = subprocess.run(
            list(spec.args),
            input=spec.stdin_text,
            env=merged_env,
            capture_output=True,
            text=True,
            timeout=spec.timeout_seconds,
            check=False,
        )
        return CommandResult(res.returncode, res.stdout, res.stderr)


class UrllibHttpAdapter:
    def send(self, request: HttpRequest) -> HttpResponse:
        req = urllib.request.Request(
            request.url, data=request.body, headers=request.headers, method=request.method,
        )
        try:
            with urllib.request.urlopen(req, timeout=request.timeout_seconds) as resp:
                return HttpResponse(resp.status, resp.read().decode("utf-8", errors="replace"))
        except urllib.error.HTTPError as err:
            return HttpResponse(err.code, err.read().decode("utf-8", errors="replace"))
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            return HttpResponse(0, "")


def run_suite(env: ChaosEnvironment, scenario_name: str) -> list[ScenarioOutcome]:
    if scenario_name == "all":
        return [handler(env) for handler in SCENARIOS.values()]
    handler = SCENARIOS.get(scenario_name)
    if handler is None:
        raise ValueError(f"Unknown scenario: {scenario_name}")
    return [handler(env)]


def build_scorecard(spec: ScorecardSpec) -> dict[str, object]:
    any_data_loss = any(item.data_loss for item in spec.outcomes)
    passed_count = sum(1 for item in spec.outcomes if item.passed)
    failed_count = len(spec.outcomes) - passed_count
    overall_pass = (
        bool(spec.outcomes)
        and failed_count == 0
        and not any_data_loss
        and spec.image_verified
    )
    max_mttr = max((item.mttr_seconds for item in spec.outcomes), default=0.0)
    return {
        "schema": SCORECARD_SCHEMA,
        "generated_at_utc": spec.generated_at_utc,
        "image": spec.config.image,
        "image_verified": spec.image_verified,
        "mttr_budget_seconds": spec.config.mttr_budget_seconds,
        "overall_gate": "pass" if overall_pass else "fail",
        "passed_count": passed_count,
        "failed_count": failed_count,
        "any_data_loss": any_data_loss,
        "max_mttr_seconds": round(max_mttr, 3),
        "scenarios": [asdict(item) for item in spec.outcomes],
    }


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--scenario", default="all", choices=["all", *SCENARIOS.keys()])
    parser.add_argument("--image", default="honua-server:local", help="Honua Server image tag")
    parser.add_argument("--compose-file", default="docker-compose.yml", help="Compose file path")
    parser.add_argument("--base-url", default="http://127.0.0.1:8080", help="Primary server URL")
    parser.add_argument("--replica-url", default="http://127.0.0.1:8082", help="Replica URL")
    parser.add_argument("--rotated-token", default="", help="Signed JWT for rotated-key probe")
    parser.add_argument("--mttr-budget-seconds", type=float, default=30.0)
    parser.add_argument("--scorecard", type=Path, help="Optional JSON scorecard output path")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None, env: ChaosEnvironment | None = None) -> int:
    args = parse_args(argv)
    config = ScenarioConfig(
        image=args.image,
        compose_file=args.compose_file,
        base_url=args.base_url.rstrip("/"),
        replica_url=args.replica_url.rstrip("/"),
        rotated_token=args.rotated_token,
        mttr_budget_seconds=args.mttr_budget_seconds,
    )
    active_env = env or ChaosEnvironment(
        config=config, command=SubprocessCommandAdapter(), http=UrllibHttpAdapter(),
    )
    outcomes = run_suite(active_env, args.scenario)
    image_verified = verify_running_image(active_env)
    scorecard = build_scorecard(
        ScorecardSpec(active_env.config, outcomes, active_env.clock.utc_iso(), image_verified),
    )
    rendered = json.dumps(scorecard, indent=2, sort_keys=True)
    if args.scorecard is not None:
        args.scorecard.parent.mkdir(parents=True, exist_ok=True)
        args.scorecard.write_text(rendered + "\n", encoding="utf-8")
    print(rendered)
    return 0 if scorecard["overall_gate"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
