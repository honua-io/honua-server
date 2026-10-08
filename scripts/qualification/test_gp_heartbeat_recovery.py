"""Bounded heartbeat rehearsal failure handling; no runtime qualification credit."""

import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path

from test_gp_store_crash import functions

ROOT = Path(__file__).resolve().parents[2]
RECOVERY = ROOT / "scripts/qualification/gp-heartbeat-recovery.sh"
HARNESS = ROOT / "scripts/qualification/gp-lifecycle-harness.sh"


class HeartbeatRecoveryTests(unittest.TestCase):
    def run_shell(self, script, **environment):
        with tempfile.TemporaryDirectory() as directory:
            result = subprocess.run(["bash", "-c", script], cwd=directory,
                                    env={**os.environ, **environment}, capture_output=True,
                                    text=True, check=False)
            self.assertEqual(0, result.returncode, result.stderr + result.stdout)

    def test_failed_observation_unpauses_both_workers_without_passing(self):
        script = functions(RECOVERY, "run_heartbeat_recovery") + r'''
set -uo pipefail
barrier_root="$PWD/barriers"; receipt_root="$PWD"; project_name=owned
scenario_finding='observation failed'; runtime_taint=''
run_heartbeat_recovery_case() {
  first_container=first; second_container=second; paused=1; job=job-1
  mkdir -p "$first_root/$job"
  echo '{"workerId":"original"}' > "$first_root/$job/native-process-started.ready.json"
  return 17
}
docker() { echo "$*" >> calls; }
compose() { echo "$*" >> calls; }
wait_ready() { return 0; }; wait_peer_ready() { return 0; }
write_receipt() { echo invalid-pass > passed; }
result=0; run_heartbeat_recovery || result=$?
[[ "$result" == 17 && ! -e passed && -z "$runtime_taint" ]] || exit 71
grep -qx 'unpause first' calls || exit 72
grep -qx 'rm -f second' calls || exit 73
grep -qx 'up -d --force-recreate worker' calls || exit 74
[[ -s stale-lease-original-native-process-started.ready.json ]] || exit 75
'''
        self.run_shell(script)

    def test_cleanup_failure_taints_following_scenarios_but_allows_final_cleanup(self):
        script = (functions(RECOVERY, "run_heartbeat_recovery") + "\n"
                  + functions(HARNESS, "run_scenario") + r'''
set -uo pipefail
barrier_root="$PWD/barriers"; receipt_root="$PWD"; project_name=owned
runtime_taint=""; preflight_failure=""; lane=self-test
scenario_state_reset() { scenario_name="$1"; scenario_finding=""; }
run_heartbeat_recovery_case() { first_container=first; paused=1; return 0; }
docker() { [[ "$1" != unpause ]]; }
compose() { return 0; }; wait_ready() { return 0; }; wait_peer_ready() { return 0; }
write_receipt() { printf '%s\n%s\n' "$2" "$3" > "$receipt_root/$1.json"; }
following() { echo incorrect > following-ran; }
cleanup() { echo complete > cleanup-ran; }
run_scenario stale-lease run_heartbeat_recovery && exit 71
run_scenario following following && exit 72
run_scenario cleanup cleanup || exit 73
[[ ! -e following-ran && -e cleanup-ran ]] || exit 74
grep -q '^fail$' stale-lease.json || exit 75
grep -q 'not executed: heartbeat recovery topology restoration failed' following.json || exit 76
''')
        self.run_shell(script)

    def test_natural_elapsed_time_and_retry_budget_are_required(self):
        original = {"lastHeartbeatAt": "2026-09-25T20:00:00Z", "attemptCount": 1}
        retry = {"updatedAt": "2026-09-25T20:01:31Z", "nextRetryAt": "2026-09-25T20:02:01Z",
                 "attemptCount": 1, "artifactReferences": [], "claimedBy": None}
        variants = [(retry, True)]
        variants.append(({key: value for key, value in retry.items() if key != "claimedBy"}, True))
        for change in ({"updatedAt": "2026-09-25T20:01:00Z"},
                       {"nextRetryAt": "2026-09-25T20:01:32Z"},
                       {"attemptCount": 2}, {"artifactReferences": ["leaked"]},
                       {"claimedBy": "old-worker"}):
            variants.append(({**retry, **change}, False))
        for candidate, expected in variants:
            with self.subTest(candidate=candidate):
                script = functions(RECOVERY, "heartbeat_validate_retry") + r'''
printf '%s' "$ORIGINAL" > original.json
printf '%s' "$RETRY" > retry.json
result=0; heartbeat_validate_retry original.json retry.json || result=$?
if [[ "$EXPECTED" == pass ]]; then [[ "$result" == 0 ]]; else [[ "$result" != 0 ]]; fi
'''
                self.run_shell(script, ORIGINAL=json.dumps(original), RETRY=json.dumps(candidate),
                               EXPECTED="pass" if expected else "fail")

    def test_completed_record_comparison_detects_stale_metadata_writes(self):
        winner = {"status": 3, "attemptCount": 2, "claimedBy": "winner", "version": 11,
                  "currentPhase": "Completed", "percentComplete": 100, "warnings": [],
                  "artifactReferences": ["winning-reference"], "updatedAt": "terminal-time",
                  "spec": {"operation": "original"}}
        mutations = [{"version": 12}, {"currentPhase": "Running"}, {"percentComplete": 15},
                     {"warnings": ["stale-warning"]}, {"updatedAt": "stale-time"},
                     {"spec": {"operation": "stale"}}, {"artifactReferences": ["stale-reference"]}]
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                script = functions(RECOVERY, "heartbeat_winner_projection") + r'''
[[ "$(heartbeat_winner_projection <<<"$WINNER")" != "$(heartbeat_winner_projection <<<"$STALE")" ]]
'''
                self.run_shell(script, WINNER=json.dumps(winner), STALE=json.dumps({**winner, **mutation}))

    def test_resilience_uses_natural_recovery_helper_and_propagates_failure(self):
        script = functions(HARNESS, "run_stale_lease") + r'''
repo_root="$PWD"
mkdir -p scripts/qualification
echo 'run_heartbeat_recovery() { echo invoked > natural; return 17; }' > scripts/qualification/gp-heartbeat-recovery.sh
result=0; run_stale_lease || result=$?
[[ -e natural && "$result" == 17 ]]
'''
        self.run_shell(script)


if __name__ == "__main__":
    unittest.main()
