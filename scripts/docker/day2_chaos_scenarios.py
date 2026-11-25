"""Deterministic Day-2 chaos, upgrade, and self-healing scenario implementations."""

from __future__ import annotations

from collections.abc import Callable

from day2_chaos_support import (
    BOUNDED_DISK_FULL_STATUSES,
    PG_DB,
    PG_USER,
    REPLICA_CONVERGENCE_BUDGET_SECONDS,
    ChaosEnvironment,
    ClockAdapter,
    CommandAdapter,
    CommandResult,
    CommandSpec,
    HttpAdapter,
    HttpRequest,
    HttpResponse,
    OutcomeSpec,
    ProbeSpec,
    ScenarioConfig,
    ScenarioOutcome,
    build_probe_jwt,
    compose_run,
    evaluate_outcome,
    http_get,
    http_post,
    parse_feature_count,
    parse_json_int,
    parse_json_str,
    parse_services_count,
    psql_exec,
    verify_running_image,
    wait_for_probe,
)

__all__ = [
    "SCENARIOS",
    "ChaosEnvironment",
    "ClockAdapter",
    "CommandAdapter",
    "CommandResult",
    "CommandSpec",
    "HttpAdapter",
    "HttpRequest",
    "HttpResponse",
    "ScenarioConfig",
    "ScenarioOutcome",
    "build_probe_jwt",
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
    baseline_count = parse_services_count(baseline.body)
    restarted = compose_run(env, ("restart", "postgres"))
    live_during = http_get(env, f"{env.config.base_url}/healthz/live")
    recovered = wait_for_probe(env, ProbeSpec(f"{env.config.base_url}/healthz/ready"))
    after = http_get(env, f"{env.config.base_url}/rest/services?f=json")
    after_count = parse_services_count(after.body)
    checks = {
        "baseline_ok": baseline.status == 200 and baseline_count >= 0,
        "postgres_restarted": restarted.returncode == 0,
        "live_during_restart": live_during.status == 200,
        "recovered_ready_200": recovered.reached,
        "post_restart_read_ok": after.status == 200 and after_count >= 0,
    }
    data_loss = after.status != 200 or after_count < max(baseline_count, 0)
    return evaluate_outcome(OutcomeSpec(
        "postgres_restart_mid_traffic_auto_recovers", "mid-traffic-restart",
        recovered.elapsed_seconds, env.config.mttr_budget_seconds, data_loss,
        checks, f"baseline_services={baseline_count}, after_services={after_count}",
    ))


def scenario_live_ddl_cached_plan_invalidation(env: ChaosEnvironment) -> ScenarioOutcome:
    """Live ALTER TABLE invalidates cached plans and Honua self-heals queries."""
    query_url = f"{env.config.base_url}/rest/services/day2_parcels/FeatureServer/0/query?where=1%3D1&outFields=*&f=json"
    warm_1, warm_2 = http_get(env, query_url), http_get(env, query_url)
    before_count = parse_feature_count(warm_2.body)
    ddl = psql_exec(env, "ALTER TABLE day2_parcels ADD COLUMN IF NOT EXISTS day2_note text DEFAULT 'ok';")
    healed = wait_for_probe(env, ProbeSpec(query_url))
    after_count = parse_feature_count(healed.last_body)
    checks = {
        "warm_queries_ok": warm_1.status == 200 and warm_2.status == 200 and before_count >= 0,
        "live_ddl_applied": ddl.returncode == 0,
        "post_ddl_query_healed": healed.reached and after_count >= 0,
    }
    data_loss = not healed.reached or after_count < max(before_count, 0)
    return evaluate_outcome(OutcomeSpec(
        "live_ddl_cached_plan_invalidation_auto_heals", "cached-plan-invalidation",
        healed.elapsed_seconds, env.config.mttr_budget_seconds, data_loss,
        checks, f"before_features={before_count}, after_features={after_count}",
    ))


def scenario_backup_wipe_restore_secrets_and_layers(env: ChaosEnvironment) -> ScenarioOutcome:
    """Backup, wipe, and restore preserves encrypted connections and published layers."""
    before_count = parse_services_count(http_get(env, f"{env.config.base_url}/rest/services?f=json").body)
    backup = compose_run(env, ("exec", "-T", "postgres", "pg_dump", "-U", PG_USER, PG_DB))
    if backup.returncode != 0 or not backup.stdout.strip():
        return evaluate_outcome(OutcomeSpec(
            "backup_wipe_restore_preserves_encrypted_secrets_and_layers", "backup-restore", 0.0,
            env.config.mttr_budget_seconds, False,
            {"backup_succeeded": False, "aborted_before_wipe": True},
            f"backup failed before wipe (rc={backup.returncode}, bytes={len(backup.stdout)})",
        ))
    wipe = psql_exec(env, "DROP SCHEMA public CASCADE; CREATE SCHEMA public;")
    restore = compose_run(
        env, ("exec", "-T", "postgres", "psql", "-v", "ON_ERROR_STOP=1", "-U", PG_USER, "-d", PG_DB),
        stdin_text=backup.stdout,
    )
    recovered = wait_for_probe(env, ProbeSpec(f"{env.config.base_url}/healthz/ready"))
    after_layers = http_get(env, f"{env.config.base_url}/rest/services?f=json")
    secrets_probe = http_get(env, f"{env.config.base_url}/api/v1/admin/connections")
    after_count = parse_services_count(after_layers.body)
    secrets_ok = secrets_probe.status == 200 and parse_json_int(secrets_probe.body, "decrypted", 0) > 0
    layers_ok = after_layers.status == 200 and after_count >= before_count > 0
    checks = {
        "backup_succeeded": True, "wipe_succeeded": wipe.returncode == 0,
        "restore_succeeded": restore.returncode == 0, "ready_after_restore": recovered.reached,
        "layers_preserved": layers_ok, "encrypted_secrets_preserved": secrets_ok,
    }
    return evaluate_outcome(OutcomeSpec(
        "backup_wipe_restore_preserves_encrypted_secrets_and_layers", "backup-restore",
        recovered.elapsed_seconds, env.config.mttr_budget_seconds, not (layers_ok and secrets_ok),
        checks, f"services={before_count}->{after_count}, secrets_status={secrets_probe.status}",
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


def _resolve_rotated_token(env: ChaosEnvironment, rotate_body: str) -> str:
    if env.config.rotated_token:
        return env.config.rotated_token
    issued = parse_json_str(rotate_body, "token")
    if issued:
        return issued
    kid = parse_json_str(rotate_body, "kid") or "rotated-k1"
    return build_probe_jwt(kid, env.config.jwks_url)


def scenario_jwks_key_rotation_rate_limited(env: ChaosEnvironment) -> ScenarioOutcome:
    """JWKS rotation auto-refreshes valid keys while rate-limiting unknown key ID bursts."""
    rotated = http_post(env, f"{env.config.jwks_url}/rotate")
    status_url = f"{env.config.base_url}/api/v1/admin/status"
    valid_jwt = _resolve_rotated_token(env, rotated.body)
    accepted = wait_for_probe(env, ProbeSpec(status_url, headers={"Authorization": f"Bearer {valid_jwt}"}))
    burst = [
        env.http.send(
            HttpRequest("GET", status_url, {"Authorization": f"Bearer {build_probe_jwt(f'unknown-kid-{i}', env.config.jwks_url)}"})
        ).status
        for i in range(5)
    ]
    refresh_count = parse_json_int(http_get(env, f"{env.config.jwks_url}/metrics").body, "refresh_count", 99)
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
