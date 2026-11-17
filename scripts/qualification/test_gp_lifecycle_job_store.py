"""The GP lifecycle harness reads jobs from PostgreSQL and holds the native child for its probes."""

import base64
import hashlib
import hmac
import json
import os
import re
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
HARNESS = ROOT / "scripts/qualification/gp-lifecycle-harness.sh"
JOB_STORE = ROOT / "scripts/qualification/gp-job-store.sh"
COMPOSE = ROOT / "docker/gp-reliability/compose.yml"
JOB_STATUSES = re.search(r"^job_statuses=.*$", JOB_STORE.read_text(encoding="utf-8"), re.M).group(0) + "\n"
LIFECYCLE_FUNCTIONS = ("cancel_barrier_job", "run_timeout_case", "run_duplicate_delivery")


def harness_function(name):
    source = HARNESS.read_text(encoding="utf-8") + JOB_STORE.read_text(encoding="utf-8")
    match = re.search(rf"^{name}\(\) \{{ .*? \}}$", source, re.M) or re.search(
        rf"^{name}\(\) \{{\n.*?^\}}", source, re.M | re.S)
    if match is None:
        raise AssertionError(f"{name} is not defined in the harness")
    return match.group(0)


def run_bash(script, directory):
    return subprocess.run(["bash", "-c", script], cwd=directory, env=dict(os.environ),
                          capture_output=True, text=True, check=False)


class JobStoreTests(unittest.TestCase):
    def test_lifecycle_scenarios_exec_only_into_the_job_database(self):
        for name in LIFECYCLE_FUNCTIONS + ("job_sql",):
            with self.subTest(function=name):
                services = set(re.findall(r"compose exec -T (\S+)", harness_function(name)))
                self.assertLessEqual(services, {"postgres"})

    def test_every_qualification_script_reads_job_state_only_from_the_job_database(self):
        for script in sorted(HARNESS.parent.glob("gp-*.sh")):
            with self.subTest(script=script.name):
                services = set(re.findall(r"compose exec -T (?!--user)(\S+)", script.read_text(encoding="utf-8")))
                self.assertLessEqual(services, {"postgres"})

    def test_job_record_overlays_the_database_columns_on_the_stored_record(self):
        functions = "\n".join(harness_function(name) for name in ("job_sql", "job_id_is_safe", "job_record"))
        with tempfile.TemporaryDirectory() as directory:
            completed = run_bash(functions + r'''
compose() { printf '%s\n' "$*" >> calls; echo call >> count; echo '{"attemptCount":2,"timeoutPolicy":{"maxDuration":"00:00:02"}}'; }
record="$(job_record gp-1)" || exit 61
[[ "$(jq -r .attemptCount <<<"$record")" == 2 ]] || exit 62
grep -q "FROM honua.execution_jobs WHERE operation_id = 'gp-1'" calls || exit 63
grep -q "'attemptCount', attempt_count" calls || exit 64
job_record "gp-1' OR '1'='1" && exit 65
[[ "$(wc -l < count)" -eq 1 ]] || exit 66
compose() { :; }
job_record gp-missing && exit 67
exit 0
''', directory)
            self.assertEqual(0, completed.returncode, completed.stderr)

    def test_job_record_carries_the_status_ordinal_the_store_serializes(self):
        functions = "\n".join(harness_function(name) for name in ("job_sql", "job_id_is_safe", "job_record"))
        with tempfile.TemporaryDirectory() as directory:
            completed = run_bash(JOB_STATUSES + functions + r'''
compose() { printf '%s\n' "$*" >> calls; echo '{}'; }
job_record gp-1 >/dev/null || exit 61
grep -q "array_position(ARRAY\['Queued', 'Provisioning', 'Running', 'Succeeded', 'Failed', 'Cancelled'\], status) - 1" calls || exit 62
grep -q "'nextRetryAt', next_retry_at" calls || exit 63
grep -q "'currentPhase', current_phase" calls || exit 64
''', directory)
            self.assertEqual(0, completed.returncode, completed.stderr)

    def test_job_store_helpers_refuse_unsafe_ids_without_touching_the_database(self):
        helpers = ("result_package_record", "result_package_exists", "expire_terminal_job", "job_row_exists",
                   "strand_exhausted_claim")
        functions = "\n".join(harness_function(name) for name in ("job_sql", "job_id_is_safe") + helpers)
        with tempfile.TemporaryDirectory() as directory:
            completed = run_bash(functions + "\ncompose() { echo called >> calls; }\n" + "".join(
                f"{name} \"gp-1' OR '1'='1\" && exit 70\n" for name in helpers)
                + "insert_poison_job poison-1 \"gp-1'\" && exit 71\n[[ ! -e calls ]] || exit 72\n", directory)
            self.assertEqual(0, completed.returncode, completed.stderr)

    def test_stranded_claim_spends_the_only_attempt_on_a_dead_worker(self):
        functions = "\n".join(harness_function(name) for name in ("job_sql", "job_id_is_safe", "strand_exhausted_claim"))
        with tempfile.TemporaryDirectory() as directory:
            completed = run_bash(functions + r'''
compose() { printf '%s\n' "$*" >> calls; echo gp-1; }
strand_exhausted_claim gp-1 || exit 61
grep -q "jsonb_build_object('maxAttempts', 1)" calls || exit 62
grep -q "status = 'Running'.*attempt_count = 1, claimed_by = 'dead-worker'" calls || exit 63
grep -q "queue_visible_at = NULL, queue_priority = NULL" calls || exit 64
grep -q "WHERE operation_id = 'gp-1' AND status = 'Queued'" calls || exit 65
compose() { echo other; }
strand_exhausted_claim gp-1 && exit 66
exit 0
''', directory)
            self.assertEqual(0, completed.returncode, completed.stderr)

    def test_poison_row_copies_a_queued_job_and_is_placed_ahead_of_it(self):
        functions = "\n".join(harness_function(name) for name in ("job_sql", "job_id_is_safe", "insert_poison_job"))
        with tempfile.TemporaryDirectory() as directory:
            completed = run_bash(functions + r'''
compose() { printf '%s\n' "$*" >> calls; echo poison-1; }
insert_poison_job poison-1 gp-1 || exit 61
grep -q "queue_visible_at - interval '1 minute'" calls || exit 62
grep -q "json_build_object('poison', true)" calls || exit 63
grep -q "WHERE operation_id = 'gp-1' AND queue_visible_at IS NOT NULL" calls || exit 64
exit 0
''', directory)
            self.assertEqual(0, completed.returncode, completed.stderr)

    def tenant_tokens(self, script):
        functions = "\n".join(harness_function(name) for name in ("qualification_tenant_tokens", "mint_tenant_token"))
        env = {k: v for k, v in os.environ.items() if not k.startswith(("HONUA_GP_TENANT_", "HONUA_GP_OIDC_"))}
        with tempfile.TemporaryDirectory() as directory:
            return subprocess.run(["bash", "-c", functions + script], cwd=directory, env=env,
                                  capture_output=True, text=True, check=False)

    def test_resilience_run_without_an_identity_provider_mints_signed_tenant_tokens(self):
        completed = self.tenant_tokens(r'''
qualification_tenant_tokens || exit 61
for token in "$HONUA_GP_TENANT_A_TOKEN" "$HONUA_GP_TENANT_B_TOKEN"; do printf '%s\n' "$token"; done
printf '%s\n' "$HONUA_GP_OIDC_SIGNING_KEY" "$HONUA_GP_OIDC_ISSUER" "$HONUA_GP_OIDC_AUDIENCE"
''')
        self.assertEqual(0, completed.returncode, completed.stderr)
        token_a, token_b, key, issuer, audience = completed.stdout.split()
        self.assertGreaterEqual(len(key), 32)
        for token, tenant in ((token_a, "tenant-a"), (token_b, "tenant-b")):
            header, payload, signature = token.split(".")
            expected = base64.urlsafe_b64encode(
                hmac.new(key.encode(), f"{header}.{payload}".encode(), hashlib.sha256).digest()).rstrip(b"=").decode()
            self.assertEqual(expected, signature)
            claims = json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))
            self.assertEqual((tenant, issuer, audience), (claims["tenant_id"], claims["iss"], claims["aud"]))

    def test_resilience_run_refuses_a_partial_identity_configuration(self):
        completed = self.tenant_tokens(r'''
HONUA_GP_TENANT_A_TOKEN=supplied
qualification_tenant_tokens && exit 61
[[ "$preflight_failure" == "HONUA_GP_TENANT_B_TOKEN is required when any tenant identity value is supplied" ]] || exit 62
''')
        self.assertEqual(0, completed.returncode, completed.stderr)

    def test_stale_worker_counts_a_lost_execution_lease_as_observed_stale_ownership(self):
        source = (HARNESS.parent / "gp-heartbeat-recovery.sh").read_text(encoding="utf-8")
        function = re.search(r"^heartbeat_saw_stale_ownership\(\) \{\n.*?^\}", source, re.M | re.S).group(0)
        with tempfile.TemporaryDirectory() as directory:
            completed = run_bash(function + r'''
job=gp-1
echo "warn: Execution lease lost for job gp-1 on worker w-1; cancelling the stale attempt" > lease.log
echo "warn: Skipping state transition for job gp-1: current status is Running" > skip.log
echo "warn: Execution lease lost for job gp-2 on worker w-1" > other.log
heartbeat_saw_stale_ownership lease.log || exit 61
heartbeat_saw_stale_ownership skip.log || exit 62
heartbeat_saw_stale_ownership other.log && exit 63
exit 0
''', directory)
            self.assertEqual(0, completed.returncode, completed.stderr)

    def test_duplicate_delivery_requeues_the_running_row_and_fences_the_first_claim(self):
        functions = "\n".join(harness_function(name) for name in ("job_sql", "job_id_is_safe", "requeue_claimed_job"))
        with tempfile.TemporaryDirectory() as directory:
            completed = run_bash(functions + r'''
compose() { printf '%s\n' "$*" >> calls; echo gp-1; }
requeue_claimed_job gp-1 || exit 61
grep -q "SET status = 'Queued', version = version + 1" calls || exit 62
grep -q "status IN ('Provisioning', 'Running')" calls || exit 63
compose() { :; }
requeue_claimed_job gp-1 && exit 64
exit 0
''', directory)
            self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertIn("requeue_claimed_job", harness_function("run_duplicate_delivery"))

    def test_native_entry_hold_wraps_the_scenario_and_restores_the_worker(self):
        functions = "\n".join(harness_function(name) for name in (
            "native_entry_hold_active", "keep_held_worker_logs", "with_native_entry_hold"))
        with tempfile.TemporaryDirectory() as directory:
            completed = run_bash(functions + r'''
runtime_taint=""; receipt_root="$PWD"; scenario_name=held
scenario_fail() { echo "$1" > finding; }
compose() { printf 'compose %s pause=%s\n' "$*" "${HONUA_GP_QUALIFICATION_PAUSE_AT:-}" >> calls; }
probe() { native_entry_hold_active && echo "probe $1" >> calls; return "$2"; }
with_native_entry_hold probe a 0 || exit 61
with_native_entry_hold probe b 7; [[ $? == 7 ]] || exit 62
[[ -z "${HONUA_GP_QUALIFICATION_PAUSE_AT:-}" && -z "$runtime_taint" ]] || exit 63
expected='compose up -d --force-recreate worker pause=native-process-started
probe a
compose up -d --force-recreate worker pause=
compose up -d --force-recreate worker pause=native-process-started
probe b
compose logs --no-color --tail 1000 worker pause=native-process-started
compose up -d --force-recreate worker pause='
[[ -e held.held-worker.log ]] || exit 65
[[ "$(cat calls)" == "$expected" ]] || { cat calls >&2; exit 64; }
exit 0
''', directory)
            self.assertEqual(0, completed.returncode, completed.stderr)

    def test_native_child_scenarios_run_under_the_entry_hold(self):
        source = HARNESS.read_text(encoding="utf-8")
        for scenario in ("cancel-native-process-started", "mcp-cancel-native-process-started"):
            with self.subTest(scenario=scenario):
                self.assertRegex(source, rf"run_scenario {scenario} with_native_entry_hold ")
        self.assertIn("local -x HONUA_GP_QUALIFICATION_PAUSE_AT=native-process-started", harness_function("run_timeout_case"))
        self.assertIn("HONUA_GP_QUALIFICATION_PAUSE_AT: ${HONUA_GP_QUALIFICATION_PAUSE_AT:-}",
                      COMPOSE.read_text(encoding="utf-8"))

    def test_barrier_root_is_not_under_the_var_run_link(self):
        # The native entry hold refuses a barrier root with a linked ancestor; /var/run links to /run.
        for path in [COMPOSE, *sorted((ROOT / "scripts/qualification").glob("gp-*.sh"))]:
            with self.subTest(path=path.name):
                self.assertNotIn("/var/run/honua/qualification", path.read_text(encoding="utf-8"))
        self.assertIn(":/run/honua/qualification", COMPOSE.read_text(encoding="utf-8"))

    def test_held_cancellation_does_not_wait_for_the_unwritten_claimed_barrier(self):
        with tempfile.TemporaryDirectory() as directory:
            completed = run_bash(harness_function("native_entry_hold_active") + "\n" + harness_function("cancel_barrier_job") + r'''
export HONUA_GP_QUALIFICATION_PAUSE_AT=native-process-started
now() { echo t; }
submit_async() { echo gp-1; }
wait_barrier() { echo "wait $2" >> calls; [[ "$2" != claimed ]]; }
release_barrier() { echo "release $2" >> calls; }
barrier_record() { echo '{"childProcessId":""}'; }
compose() { echo worker; }
scenario_fail() { echo "$1" > finding; return 1; }
cancel_barrier_job native-process-started
grep -q "wait claimed\|release claimed" calls && exit 61
grep -q "wait native-process-started" calls || exit 62
grep -q "did not report a child PID" finding || exit 63
exit 0
''', directory)
            self.assertEqual(0, completed.returncode, completed.stderr)


if __name__ == "__main__":
    unittest.main()
