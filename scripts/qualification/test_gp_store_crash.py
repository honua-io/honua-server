"""Failure isolation for the real staged-store qualification adapter (mock IO)."""

import os
import re
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CRASH = ROOT / "scripts/qualification/gp-store-crash.sh"
HARNESS = ROOT / "scripts/qualification/gp-lifecycle-harness.sh"


def functions(path, *names):
    source = path.read_text(encoding="utf-8")
    return "\n".join(re.search(rf"^{name}\(\) \{{\n.*?^\}}", source, re.M | re.S).group(0)
                     for name in names)


class StagedStoreCrashTests(unittest.TestCase):
    def run_shell(self, script, **environment):
        with tempfile.TemporaryDirectory() as directory:
            result = subprocess.run(["bash", "-c", script], cwd=directory,
                                    env={**os.environ, **environment}, capture_output=True,
                                    text=True, check=False)
            self.assertEqual(0, result.returncode, result.stderr + result.stdout)

    def test_resilience_cell_uses_actual_staged_store_boundary(self):
        script = functions(HARNESS, "run_output_write_failure") + r'''
set -uo pipefail
repo_root="$PWD"
mkdir -p scripts/qualification
cat > scripts/qualification/gp-store-crash.sh <<'SH'
run_store_crash_boundary() { printf '%s\n' "$*" > invoked; return 19; }
SH
result=0
run_output_write_failure || result=$?
[[ "$result" == 19 ]] || exit 71
[[ "$(cat invoked)" == 'native-process-started store' ]] || exit 72
'''
        self.run_shell(script)

    def test_early_case_failures_restore_caller_environment(self):
        script = functions(CRASH, "run_store_crash_boundary", "run_store_crash_case") + r'''
set -uo pipefail
scenario_name=output-write-failure; scenario_finding=""; scenario_cleanup_failure=""
runtime_taint=""; scenario_evidence_file="$PWD/evidence"; native_payload='{}'
unset HONUA_GP_QUALIFICATION_BARRIER_ROOT HONUA_GP_WORKER_REDIS
if [[ "$INITIAL" == custom ]]; then
  export HONUA_GP_QUALIFICATION_BARRIER_ROOT=/caller HONUA_GP_WORKER_REDIS=caller:6379
fi
environment_state() {
  printf '%s\n' "${HONUA_GP_QUALIFICATION_BARRIER_ROOT+x}:${HONUA_GP_QUALIFICATION_BARRIER_ROOT-}" \
    "${HONUA_GP_WORKER_REDIS+x}:${HONUA_GP_WORKER_REDIS-}"
}
environment_state > before
compose() {
  if [[ "$*" == 'up -d --force-recreate server server-peer worker' ]]; then
    environment_state > restored
  elif [[ "$FAILURE" == setup ]]; then return 31; fi
}
submit_async() { [[ "$FAILURE" != submit ]] || return 32; echo job-1; }
jq() { echo '{}'; }
wait_barrier() { return 33; }
scenario_fail() { scenario_finding="$1"; return 1; }
release_barrier() { echo "$*" >> released; }
wait_ready() { return 0; }
wait_peer_ready() { return 0; }
write_receipt() { echo incorrect-pass > passed; }
run_store_crash_boundary output-bytes-written-unpublished store && exit 73
environment_state > after
cmp before after && cmp before restored || exit 74
[[ ! -e passed && -z "$runtime_taint" ]] || exit 75
'''
        for initial in ("unset", "custom"):
            for failure in ("setup", "submit", "barrier"):
                with self.subTest(initial=initial, failure=failure):
                    self.run_shell(script, INITIAL=initial, FAILURE=failure)

    def test_cleanup_failure_taints_following_scenarios_and_never_passes(self):
        script = (functions(CRASH, "run_store_crash_boundary")
                  + "\n" + functions(HARNESS, "run_scenario") + r'''
set -uo pipefail
runtime_taint=""; preflight_failure=""; lane=self-test; receipt_root="$PWD"
scenario_state_reset() { scenario_name="$1"; scenario_finding=""; scenario_cleanup_failure=""; }
run_store_crash_case() { job=job-1; store_hidden=1; scenario_finding='case assertion'; return 17; }
restore_crash_output_store() { echo attempted > restored; [[ "$RESTORE_FAILS" != yes ]]; }
release_barrier() { return 0; }
compose() { echo recreated > recreated; }
wait_ready() { return 0; }
wait_peer_ready() { return 0; }
write_receipt() { printf '%s\n%s\n' "$2" "$3" > "$receipt_root/$1.json"; }
follow_up() { echo executed > next-executed; }
cleanup() { echo executed > cleanup-executed; }
run_scenario output-write-failure run_store_crash_boundary output-bytes-written-unpublished store && exit 71
run_scenario next follow_up || true
run_scenario cleanup cleanup || exit 72
[[ -e restored && -e recreated && -e cleanup-executed ]] || exit 73
grep -q '^fail$' output-write-failure.json || exit 74
if [[ "$RESTORE_FAILS" == yes ]]; then
  [[ ! -e next-executed ]] || exit 75
  grep -q 'not executed: staged-store qualification topology restoration failed' next.json || exit 76
else
  [[ -e next-executed && -z "$runtime_taint" ]] || exit 77
fi
''')
        for restoration in ("yes", "no"):
            with self.subTest(restoration=restoration):
                    self.run_shell(script, RESTORE_FAILS=restoration)

    def test_partial_store_moves_restore_without_overwriting_live_entries(self):
        script = functions(CRASH, "restore_crash_output_store") + r'''
set -uo pipefail
job=job-1; store_hidden=1
export TEST_ROOT="$PWD/store"
outage_root="$TEST_ROOT/.qualification-outage-$job"
mkdir -p "$outage_root"
case "$MOVED" in
  marker|both|collision) echo marker > "$outage_root/.honua-gp-store.json";;
esac
case "$MOVED" in
  bytes|both) mkdir "$outage_root/gp"; echo payload > "$outage_root/gp/output";;
esac
if [[ "$MOVED" == collision ]]; then echo existing > "$TEST_ROOT/.honua-gp-store.json"; fi
compose() {
  [[ "$*" == 'exec -T --user 0 worker sh -ec '* ]] || return 51
  shift 5
  local code="$3"
  # Map only the container mount root into this test's temporary directory;
  # execute the production restoration shell unchanged otherwise.
  code="${code//\/var\/lib\/honua\/gp-outputs/$TEST_ROOT}"
  sh -ec "$code" "${@:4}"
}
result=0
restore_crash_output_store || result=$?
if [[ "$MOVED" == collision ]]; then
  [[ "$result" != 0 && "$store_hidden" == 1 ]] || exit 71
  [[ "$(cat "$TEST_ROOT/.honua-gp-store.json")" == existing ]] || exit 72
  [[ "$(cat "$outage_root/.honua-gp-store.json")" == marker ]] || exit 73
else
  [[ "$result" == 0 && "$store_hidden" == 0 && ! -d "$outage_root" ]] || exit 74
  if [[ "$MOVED" != bytes ]]; then [[ "$(cat "$TEST_ROOT/.honua-gp-store.json")" == marker ]] || exit 75; fi
  if [[ "$MOVED" != marker ]]; then [[ "$(cat "$TEST_ROOT/gp/output")" == payload ]] || exit 76; fi
fi
'''
        for moved in ("marker", "bytes", "both", "collision"):
            with self.subTest(moved=moved):
                self.run_shell(script, MOVED=moved)

    def test_success_receipt_waits_for_topology_restoration(self):
        script = functions(CRASH, "run_store_crash_boundary") + r'''
set -uo pipefail
scenario_name=output-write-failure; scenario_finding=""; runtime_taint=""
run_store_crash_case() { job=job-1; state=successful; after_sha=checksum; }
release_barrier() { return 0; }
compose() { [[ "$RESTORE_FAILS" != yes ]] || return 19; echo done > restored; }
wait_ready() { return 0; }
wait_peer_ready() { return 0; }
write_receipt() { [[ -e restored ]] || exit 71; printf '%s\n' "$*" > passed; }
result=0
run_store_crash_boundary output-bytes-written-unpublished store || result=$?
if [[ "$RESTORE_FAILS" == yes ]]; then
  [[ "$result" != 0 && ! -e passed && -n "$runtime_taint" ]] || exit 72
else
  [[ "$result" == 0 ]] || exit 73
  grep -q 'output-write-failure pass  job-1 successful checksum' passed || exit 74
fi
'''
        for restoration in ("yes", "no"):
            with self.subTest(restoration=restoration):
                self.run_shell(script, RESTORE_FAILS=restoration)

    def test_outage_requires_successful_http_transport_and_real_denial(self):
        script = functions(CRASH, "observe_store_outage_http") + r'''
set -uo pipefail
peer_url=http://fixture; api_key=fixture; job=job-1
curl() {
  if [[ "$*" == *healthz/ready ]]; then echo "$READY"; return 0; fi
  echo "$CONTENT"
  [[ "$TRANSPORT" == ok ]]
}
result=0
observe_store_outage_http > observations.json || result=$?
jq -e --arg content "$CONTENT" '.content_http == $content' observations.json || exit 71
if [[ "$EXPECTED" == pass ]]; then [[ "$result" == 0 ]] || exit 72
else [[ "$result" != 0 ]] || exit 73; fi
'''
        for ready, content, transport, expected in (
                ("503", "503", "ok", "pass"), ("503", "404", "ok", "pass"),
                ("200", "503", "ok", "fail"), ("503", "000", "fail", "fail"),
                ("503", "200", "ok", "fail"), ("503", "206", "ok", "fail"),
                ("503", "302", "ok", "fail"), ("503", "503", "fail", "fail")):
            with self.subTest(ready=ready, content=content, transport=transport):
                self.run_shell(script, READY=ready, CONTENT=content, TRANSPORT=transport,
                               EXPECTED=expected)

    def test_write_failure_requires_real_store_stack_and_empty_retry(self):
        script = functions(CRASH, "observe_store_write_retry") + r'''
set -uo pipefail
job=job-1; scenario=output-write-failure; receipt_root="$PWD"; object_root="$PWD/store"
scenario_evidence_file="$PWD/evidence.json"; outage_started=timestamp
before_fixture_record='{"attemptCount":1}'; write_failure_record=null
mkdir "$object_root"; echo '{}' > "$scenario_evidence_file"
fixture_record='{"status":0,"attemptCount":1,"nextRetryAt":"future","artifactReferences":[],"currentPhase":"Requeued: failure"}'
case "$MUTATION" in
  earlier-attempt) fixture_record="$(jq '.attemptCount=0' <<< "$fixture_record")";;
  published) fixture_record="$(jq '.artifactReferences=["unexpected"]' <<< "$fixture_record")";;
  terminal) fixture_record="$(jq '.status=3' <<< "$fixture_record")";;
  no-retry) fixture_record="$(jq '.nextRetryAt=null' <<< "$fixture_record")";;
  restored) touch "$object_root/.honua-gp-store.json";;
esac
compose() {
  if [[ "$1" == logs ]]; then
    echo 'Job execution failed: job-1'
    echo 'Geoprocessing:OutputStaging persistence attestation is missing or mismatched'
    [[ "$MUTATION" == generic ]] || echo 'FileSystemGeoprocessingOutputObjectStore.WriteAsync'
    return 0
  fi
  echo "$fixture_record"
}
object_file_count() { echo 0; }
record_attempt() { echo "$1" > attempt; }
record_transition() { echo "$1" > transition; }
scenario_fail() { echo "$1" > finding; return 1; }
sleep() { SECONDS="$deadline"; }
result=0
observe_store_write_retry || result=$?
if [[ "$MUTATION" == none ]]; then
  [[ "$result" == 0 && "$(cat attempt)" == 1 && "$(cat transition)" == queued ]] || exit 71
  jq -e '.write_failure_retry.attemptCount == 1' "$scenario_evidence_file" || exit 72
else
  [[ "$result" != 0 && -e finding ]] || exit 73
fi
'''
        for mutation in ("none", "earlier-attempt", "published", "terminal", "no-retry",
                         "restored", "generic"):
            with self.subTest(mutation=mutation):
                self.run_shell(script, MUTATION=mutation)


if __name__ == "__main__":
    unittest.main()
