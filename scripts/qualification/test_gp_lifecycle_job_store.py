"""The GP lifecycle harness reads jobs from PostgreSQL and holds the native child for its probes."""

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
    def test_lifecycle_scenarios_read_no_job_state_from_redis(self):
        for name in LIFECYCLE_FUNCTIONS:
            with self.subTest(function=name):
                self.assertNotIn("redis-cli", harness_function(name))

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
