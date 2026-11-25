"""Deterministic Day-2 chaos, upgrade, and self-healing scenario definitions."""

from __future__ import annotations

import json
import time
from collections.abc import Callable
from dataclasses import dataclass, field
from datetime import UTC, datetime
from typing import Protocol

REPLICA_CONVERGENCE_BUDGET_SECONDS = 5.0
BOUNDED_DISK_FULL_STATUSES = frozenset({500, 503, 507})
PG_DB = "honua_dev"
PG_USER = "honua_user"


def utc_now_iso() -> str:
    return datetime.now(UTC).isoformat().replace("+00:00", "Z")


@dataclass(frozen=True)
class CommandSpec:
    args: tuple[str, ...]
    timeout_seconds: float = 60.0


@dataclass(frozen=True)
class CommandResult:
    returncode: int
    stdout: str = ""
    stderr: str = ""


@dataclass(frozen=True)
class HttpRequest:
    method: str
    url: str
    headers: dict[str, str] = field(default_factory=dict)
    body: bytes | None = None
    timeout_seconds: float = 5.0


@dataclass(frozen=True)
class HttpResponse:
    status: int
    body: str = ""
    headers: dict[str, str] = field(default_factory=dict)


@dataclass(frozen=True)
class ScenarioConfig:
    image: str = "honua-server:local"
    compose_file: str = "docker-compose.yml"
    base_url: str = "http://127.0.0.1:8080"
    replica_url: str = "http://127.0.0.1:8082"
    jwks_url: str = "http://127.0.0.1:8089"
    mttr_budget_seconds: float = 30.0
    poll_interval_seconds: float = 0.5
    max_wait_seconds: float = 60.0


@dataclass(frozen=True)
class ProbeSpec:
    url: str
    expected_status: int = 200
    max_wait_seconds: float | None = None
    headers: dict[str, str] = field(default_factory=dict)
    predicate: Callable[[HttpResponse], bool] | None = None


@dataclass(frozen=True)
class ProbeResult:
    reached: bool
    elapsed_seconds: float
    last_status: int
    last_body: str


@dataclass(frozen=True)
class OutcomeSpec:
    name: str
    category: str
    mttr_seconds: float
    budget_seconds: float
    data_loss: bool
    checks: dict[str, bool]
    details: str


@dataclass(frozen=True)
class ScenarioOutcome:
    name: str
    category: str
    passed: bool
    mttr_seconds: float
    mttr_budget_seconds: float
    within_mttr_budget: bool
    data_loss: bool
    details: str
    checks: dict[str, bool] = field(default_factory=dict)


class CommandAdapter(Protocol):
    def run(self, spec: CommandSpec) -> CommandResult: ...


class HttpAdapter(Protocol):
    def send(self, request: HttpRequest) -> HttpResponse: ...


@dataclass(frozen=True)
class ClockAdapter:
    now: Callable[[], float] = time.monotonic
    sleep: Callable[[float], None] = time.sleep
    utc_iso: Callable[[], str] = utc_now_iso


@dataclass(frozen=True)
class ChaosEnvironment:
    config: ScenarioConfig
    command: CommandAdapter
    http: HttpAdapter
    clock: ClockAdapter = field(default_factory=ClockAdapter)


def compose_run(env: ChaosEnvironment, subargs: tuple[str, ...]) -> CommandResult:
    return env.command.run(CommandSpec(("docker", "compose", "-f", env.config.compose_file, *subargs)))


def psql_exec(env: ChaosEnvironment, sql: str) -> CommandResult:
    return compose_run(env, ("exec", "-T", "postgres", "psql", "-U", PG_USER, "-d", PG_DB, "-Atc", sql))


def http_get(env: ChaosEnvironment, url: str) -> HttpResponse:
    return env.http.send(HttpRequest("GET", url))


def http_post(env: ChaosEnvironment, url: str) -> HttpResponse:
    return env.http.send(HttpRequest("POST", url))


def _load_json_dict(body: str) -> dict[str, object]:
    try:
        payload = json.loads(body)
    except json.JSONDecodeError:
        return {}
    return payload if isinstance(payload, dict) else {}


def parse_json_int(body: str, key: str, default: int = -1) -> int:
    value = _load_json_dict(body).get(key)
    return value if isinstance(value, int) and not isinstance(value, bool) else default


def parse_json_str(body: str, key: str) -> str:
    value = _load_json_dict(body).get(key)
    return value if isinstance(value, str) else ""


def wait_for_probe(env: ChaosEnvironment, spec: ProbeSpec) -> ProbeResult:
    started = env.clock.now()
    deadline = started + (spec.max_wait_seconds or env.config.max_wait_seconds)
    while True:
        last = env.http.send(HttpRequest("GET", spec.url, headers=spec.headers))
        matched = last.status == spec.expected_status and (
            spec.predicate is None or spec.predicate(last)
        )
        if matched or env.clock.now() >= deadline:
            elapsed = round(env.clock.now() - started, 3)
            return ProbeResult(matched, elapsed, last.status, last.body)
        env.clock.sleep(env.config.poll_interval_seconds)


def evaluate_outcome(spec: OutcomeSpec) -> ScenarioOutcome:
    within_budget = spec.mttr_seconds <= spec.budget_seconds
    passed = all(spec.checks.values()) and not spec.data_loss and within_budget
    return ScenarioOutcome(
        spec.name, spec.category, passed, round(spec.mttr_seconds, 3),
        round(spec.budget_seconds, 3), within_budget, spec.data_loss,
        spec.details, dict(spec.checks),
    )


def scenario_cold_start_postgres_delayed(env: ChaosEnvironment) -> ScenarioOutcome:
    """Honua boots degraded while Postgres is absent, then recovers automatically."""
    start_honua = compose_run(env, ("up", "-d", "--no-deps", "honua"))
    live = http_get(env, f"{env.config.base_url}/healthz/live")
    degraded = http_get(env, f"{env.config.base_url}/healthz/ready")
    start_pg = compose_run(env, ("up", "-d", "postgres"))
    recovered = wait_for_probe(env, ProbeSpec(f"{env.config.base_url}/healthz/ready"))
    checks = {
        "honua_started_without_db": start_honua.returncode == 0,
        "live_probe_200": live.status == 200,
        "degraded_ready_503": degraded.status == 503,
        "postgres_started": start_pg.returncode == 0,
        "recovered_ready_200": recovered.reached,
    }
    return evaluate_outcome(OutcomeSpec(
        "cold_start_postgres_delayed_then_ready", "cold-start", recovered.elapsed_seconds,
        env.config.mttr_budget_seconds, False, checks,
        f"degraded={degraded.status}, recovered={recovered.last_status}",
    ))


def scenario_concurrent_replicas_migration_lock(env: ChaosEnvironment) -> ScenarioOutcome:
    """Two replicas boot concurrently against the shared advisory migration lock."""
    pg_up = compose_run(env, ("up", "-d", "postgres"))
    scale = compose_run(env, ("up", "-d", "--scale", "honua=2", "--no-recreate", "honua"))
    primary = wait_for_probe(env, ProbeSpec(f"{env.config.base_url}/healthz/ready"))
    replica = wait_for_probe(env, ProbeSpec(f"{env.config.replica_url}/healthz/ready"))
    dup_sql = (
        "SELECT count(*) FROM (SELECT scriptname FROM schemaversions "
        "GROUP BY scriptname HAVING count(*) > 1) dup;"
    )
    journal = psql_exec(env, dup_sql)
    duplicates_zero = journal.returncode == 0 and journal.stdout.strip() == "0"
    checks = {
        "postgres_up": pg_up.returncode == 0,
        "replicas_scaled": scale.returncode == 0,
        "primary_ready": primary.reached,
        "replica_ready": replica.reached,
        "zero_duplicate_migrations": duplicates_zero,
    }
    return evaluate_outcome(OutcomeSpec(
        "concurrent_replicas_startup_migration_lock", "migration-lock",
        max(primary.elapsed_seconds, replica.elapsed_seconds),
        env.config.mttr_budget_seconds, not duplicates_zero, checks,
        f"primary={primary.last_status}, replica={replica.last_status}",
    ))


def scenario_postgres_restart_mid_traffic(env: ChaosEnvironment) -> ScenarioOutcome:
    """Postgres restarts under live read traffic and Honua auto-recovers with zero data loss."""
    baseline = http_get(env, f"{env.config.base_url}/rest/services?f=json")
    baseline_count = parse_json_int(baseline.body, "count", default=0)
    restarted = compose_run(env, ("restart", "postgres"))
    live_during = http_get(env, f"{env.config.base_url}/healthz/live")
    recovered = wait_for_probe(env, ProbeSpec(f"{env.config.base_url}/healthz/ready"))
    after = http_get(env, f"{env.config.base_url}/rest/services?f=json")
    after_count = parse_json_int(after.body, "count", default=-1)
    checks = {
        "baseline_ok": baseline.status == 200,
        "postgres_restarted": restarted.returncode == 0,
        "live_during_restart": live_during.status == 200,
        "recovered_ready_200": recovered.reached,
        "post_restart_read_ok": after.status == 200,
    }
    return evaluate_outcome(OutcomeSpec(
        "postgres_restart_mid_traffic_auto_recovers", "mid-traffic-restart",
        recovered.elapsed_seconds, env.config.mttr_budget_seconds,
        after.status != 200 or after_count < baseline_count,
        checks, f"baseline_count={baseline_count}, after_count={after_count}",
    ))


def scenario_live_ddl_cached_plan_invalidation(env: ChaosEnvironment) -> ScenarioOutcome:
    """Live ALTER TABLE invalidates cached plans and Honua self-heals queries."""
    query_url = f"{env.config.base_url}/rest/services/day2_parcels/FeatureServer/0/query?where=1%3D1&outFields=*&f=json"
    warm_1, warm_2 = http_get(env, query_url), http_get(env, query_url)
    before_count = parse_json_int(warm_2.body, "count", default=0)
    ddl = psql_exec(env, "ALTER TABLE day2_parcels ADD COLUMN IF NOT EXISTS day2_note text DEFAULT 'ok';")
    healed = wait_for_probe(env, ProbeSpec(query_url))
    after_count = parse_json_int(healed.last_body, "count", default=-1)
    checks = {
        "warm_queries_ok": warm_1.status == 200 and warm_2.status == 200,
        "live_ddl_applied": ddl.returncode == 0,
        "post_ddl_query_healed": healed.reached,
    }
    return evaluate_outcome(OutcomeSpec(
        "live_ddl_cached_plan_invalidation_auto_heals", "cached-plan-invalidation",
        healed.elapsed_seconds, env.config.mttr_budget_seconds,
        not healed.reached or after_count < before_count,
        checks, f"before_count={before_count}, after_count={after_count}",
    ))


def scenario_backup_wipe_restore_secrets_and_layers(env: ChaosEnvironment) -> ScenarioOutcome:
    """Backup, wipe, and restore preserves encrypted connections and published layers."""
    before_count = parse_json_int(http_get(env, f"{env.config.base_url}/rest/services?f=json").body, "count", 0)
    backup = compose_run(env, ("exec", "-T", "postgres", "pg_dump", "-U", PG_USER, PG_DB))
    wipe = psql_exec(env, "DROP SCHEMA public CASCADE; CREATE SCHEMA public;")
    restore = compose_run(env, ("exec", "-T", "postgres", "psql", "-U", PG_USER, "-d", PG_DB))
    recovered = wait_for_probe(env, ProbeSpec(f"{env.config.base_url}/healthz/ready"))
    after_layers = http_get(env, f"{env.config.base_url}/rest/services?f=json")
    secrets_probe = http_get(env, f"{env.config.base_url}/api/v1/admin/connections")
    after_count = parse_json_int(after_layers.body, "count", -1)
    secrets_ok = secrets_probe.status == 200 and parse_json_int(secrets_probe.body, "decrypted", 0) > 0
    layers_ok = after_layers.status == 200 and after_count >= before_count > 0
    checks = {
        "backup_succeeded": backup.returncode == 0, "wipe_succeeded": wipe.returncode == 0,
        "restore_succeeded": restore.returncode == 0, "ready_after_restore": recovered.reached,
        "layers_preserved": layers_ok, "encrypted_secrets_preserved": secrets_ok,
    }
    return evaluate_outcome(OutcomeSpec(
        "backup_wipe_restore_preserves_encrypted_secrets_and_layers", "backup-restore",
        recovered.elapsed_seconds, env.config.mttr_budget_seconds, not (layers_ok and secrets_ok),
        checks, f"layers={before_count}->{after_count}, secrets_status={secrets_probe.status}",
    ))


def scenario_tmpfs_disk_full_bounded_recovery(env: ChaosEnvironment) -> ScenarioOutcome:
    """Filling /tmp returns bounded errors without crash and recovers after cleanup."""
    fill = compose_run(env, ("exec", "-T", "honua", "sh", "-c", "dd if=/dev/zero of=/tmp/honua-chaos-fill bs=1M count=512 || true"))
    write_probe = http_post(env, f"{env.config.base_url}/api/v1/admin/diagnostics/temp-write")
    live_probe = http_get(env, f"{env.config.base_url}/healthz/live")
    cleanup = compose_run(env, ("exec", "-T", "honua", "rm", "-f", "/tmp/honua-chaos-fill"))
    recovered = wait_for_probe(env, ProbeSpec(f"{env.config.base_url}/healthz/ready"))
    post_write = http_post(env, f"{env.config.base_url}/api/v1/admin/diagnostics/temp-write")
    checks = {
        "tmpfs_filled": fill.returncode == 0,
        "bounded_error_on_disk_full": write_probe.status in BOUNDED_DISK_FULL_STATUSES,
        "process_stayed_alive": live_probe.status == 200, "tmpfs_cleaned": cleanup.returncode == 0,
        "recovered_ready_200": recovered.reached, "post_cleanup_write_ok": post_write.status == 200,
    }
    return evaluate_outcome(OutcomeSpec(
        "tmpfs_disk_full_returns_bounded_errors_and_recovers_after_cleanup", "disk-exhaustion",
        recovered.elapsed_seconds, env.config.mttr_budget_seconds, False, checks,
        f"disk_full={write_probe.status}, post_cleanup={post_write.status}",
    ))


def scenario_jwks_key_rotation_rate_limited(env: ChaosEnvironment) -> ScenarioOutcome:
    """JWKS rotation auto-refreshes valid keys while rate-limiting unknown key ID bursts."""
    rotated = http_post(env, f"{env.config.jwks_url}/rotate")
    status_url = f"{env.config.base_url}/api/v1/admin/status"
    accepted = wait_for_probe(env, ProbeSpec(status_url, headers={"Authorization": "Bearer rotated-kid-token"}))
    burst = [
        env.http.send(HttpRequest("GET", status_url, {"Authorization": f"Bearer unknown-kid-{i}"})).status
        for i in range(5)
    ]
    metrics_body = http_get(env, f"{env.config.jwks_url}/metrics").body
    refresh_count = parse_json_int(metrics_body, "refresh_count", 99)
    checks = {
        "jwks_rotated": rotated.status == 200,
        "rotated_token_accepted": accepted.reached,
        "unknown_kids_rejected_401": all(code == 401 for code in burst),
        "jwks_refresh_rate_limited": 1 <= refresh_count <= 2,
    }
    return evaluate_outcome(OutcomeSpec(
        "jwks_key_rotation_auto_refreshes_rate_limited", "jwks-rotation",
        accepted.elapsed_seconds, env.config.mttr_budget_seconds, False, checks,
        f"refresh_count={refresh_count}, burst={burst}",
    ))


def scenario_two_replica_metadata_convergence(env: ChaosEnvironment) -> ScenarioOutcome:
    """Metadata updates on primary converge on replica in under 5 seconds."""
    publish = http_post(env, f"{env.config.base_url}/api/v1/admin/metadata/refresh")
    target = parse_json_str(publish.body, "version")
    budget = min(env.config.mttr_budget_seconds, REPLICA_CONVERGENCE_BUDGET_SECONDS)
    spec = ProbeSpec(
        url=f"{env.config.replica_url}/rest/services?f=json",
        max_wait_seconds=max(budget, env.config.poll_interval_seconds),
        predicate=lambda r: bool(target) and parse_json_str(r.body, "version") == target,
    )
    converged = wait_for_probe(env, spec)
    checks = {
        "primary_metadata_published": publish.status == 200 and bool(target),
        "replica_converged_version": converged.reached,
    }
    return evaluate_outcome(OutcomeSpec(
        "two_replica_metadata_convergence_under_5s", "replica-convergence",
        converged.elapsed_seconds, budget, not converged.reached, checks,
        f"target_version={target}, elapsed={converged.elapsed_seconds}s",
    ))


SCENARIOS: dict[str, Callable[[ChaosEnvironment], ScenarioOutcome]] = {
    "cold_start_postgres_delayed_then_ready": scenario_cold_start_postgres_delayed,
    "concurrent_replicas_startup_migration_lock": scenario_concurrent_replicas_migration_lock,
    "postgres_restart_mid_traffic_auto_recovers": scenario_postgres_restart_mid_traffic,
    "live_ddl_cached_plan_invalidation_auto_heals": scenario_live_ddl_cached_plan_invalidation,
    "backup_wipe_restore_preserves_encrypted_secrets_and_layers": scenario_backup_wipe_restore_secrets_and_layers,
    "tmpfs_disk_full_returns_bounded_errors_and_recovers_after_cleanup": scenario_tmpfs_disk_full_bounded_recovery,
    "jwks_key_rotation_auto_refreshes_rate_limited": scenario_jwks_key_rotation_rate_limited,
    "two_replica_metadata_convergence_under_5s": scenario_two_replica_metadata_convergence,
}
