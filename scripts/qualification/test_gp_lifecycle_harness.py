"""Executable tests for the GP qualification receipt boundary."""

import copy
import importlib.util
import json
import math
import os
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
HARNESS = ROOT / "scripts/qualification/gp-lifecycle-harness.sh"
STREAK = ROOT / "scripts/qualification/gp-canary-streak.sh"


class GpQualificationHarnessTests(unittest.TestCase):
    def test_lifecycle_preserves_existing_scenarios_and_requires_new_mcp_case(self):
        completed, receipts, summary = self.run_harness("lifecycle", HONUA_SERVER_IMAGE="unattested:latest")
        self.assertNotEqual(0, completed.returncode)
        self.assertEqual(31, summary["declared_scenario_count"])
        self.assertIn("mcp-cancel-native-process-started", summary["declared_scenarios"])
        self.assertEqual("fail", receipts["mcp-cancel-native-process-started"]["outcome"])
        self.assertEqual([], summary["missing_scenarios"])
        self.assertEqual([], summary["duplicate_receipts"])

    def test_mcp_acknowledgement_cannot_replace_native_cessation_or_output_oracles(self):
        source = HARNESS.read_text(encoding="utf-8")
        function = re.search(r"^cancel_barrier_job\(\) \{\n.*?^\}", source, re.M | re.S).group(0)
        for mode in ("positive", "invalid-ack", "child-alive", "terminal-success", "readable-result", "late-result", "orphan-output", "worker-missing", "probe-failure", "missing-late-barrier"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as directory:
                script = function + r'''
set -uo pipefail
scenario_name=mcp-cancel-native-process-started
receipt_root="$PWD"; repo_root="$PWD"; scenario_transition_file="$PWD/transitions"
base_url=http://127.0.0.1:18080; peer_url=http://127.0.0.1:18081; api_key=control; native_payload='{}'
printf '%s\n' '{"state":"dismissed"}' > "$scenario_transition_file"
now() { echo timestamp; }
submit_async() { echo job-1; }
wait_barrier() { return 0; }
release_barrier() { return 0; }
barrier_record() {
  if [[ "$MODE" == missing-late-barrier && -e first-alive ]]; then echo null
  else echo '{"childProcessId":123}'; fi
}
compose() {
  if [[ "$1" == ps ]]; then echo worker-container
  elif [[ "$*" == *'GET '* ]]; then echo '{"attemptCount":1}'
  fi
}
docker() {
  if [[ "$1" == inspect ]]; then
    [[ "$2 $3 $4 $5" == "--type container --format {{.State.Running}}" ]] || return 98
    [[ "$MODE" != worker-missing ]] || return 1
    echo true; return 0
  fi
  if [[ ! -e first-alive ]]; then touch first-alive; return 0; fi
  [[ "$MODE" != probe-failure ]] || return 1
  [[ "$2" == worker-container && "${@: -1}" == 123 ]] || return 97
  if [[ "$MODE" == child-alive ]]; then echo alive; else echo absent; fi
}
node() {
  [[ "$1" == */mcp/cancel-durable-job.mjs && "$2" == "$peer_url" && "$3" == job-1 && "$HONUA_GP_MCP_API_KEY" == control ]] || return 1
  jq -n --arg mode "$MODE" '{schema:"honua.gp-mcp-cancellation-ack/v1",jobId:"job-1",tool:"honua_cancel_job",executionCessationProven:false,sdkVersion:"1.32.1",clientLockSha256:"5e6b28f4c29b82b799190e9e92083c7aca38b879afc7e242bee4320a711e195f",clientSource:"372d9ad1366a5fa28165e7ebbc7481e6e1ca13c4",adapterSha256:("a"*64),responses:[range(0;2)|{jobId:"job-1",status:"cancellation_requested",cancellationRequested:($mode!="invalid-ack")}]}'
}
wait_terminal() { if [[ "$MODE" == terminal-success ]]; then echo '{"status":"successful"}'; else echo '{"status":"dismissed"}'; fi; }
result_status_code() {
  if [[ "$MODE" == readable-result ]]; then echo 200
  elif [[ "$MODE" == late-result && -e late ]]; then echo 200
  else touch late; echo 410; fi
}
sleep() { return 0; }
object_file_count() { if [[ "$MODE" == orphan-output ]]; then echo 1; else echo 0; fi; }
scenario_fail() { echo "$1" > finding; return 1; }
set_scenario_evidence() { echo "$1" > evidence.json; }
write_receipt() { touch passed; }
delete_capture() { echo 'OGC fallback is forbidden' >&2; return 99; }
status=0
cancel_barrier_job native-process-started mcp || status=$?
if [[ "$MODE" == positive || "$MODE" == missing-late-barrier ]]; then
  [[ "$status" == 0 && -e passed ]] || exit 61
  jq -e '.child_process.exit_observed == true and .child_process.pid == 123 and .child_process.worker_container == "worker-container" and .object_inventory.after_retention_cleanup == 0 and .cancellation_responses.mcp_acknowledgement.executionCessationProven == false' evidence.json >/dev/null || exit 62
else
  [[ "$status" != 0 && ! -e passed && -s finding ]] || exit 63
fi
'''
                completed = subprocess.run(["bash", "-c", script], cwd=directory,
                                           env={**os.environ, "MODE": mode}, capture_output=True, text=True, check=False)
                self.assertEqual(0, completed.returncode, completed.stderr)

    def test_failed_timeout_restoration_blocks_follow_up_but_allows_final_cleanup(self):
        source = HARNESS.read_text(encoding="utf-8")
        functions = "\n".join(
            re.search(rf"^{name}\(\) \{{\n.*?^\}}", source, re.M | re.S).group(0)
            for name in ("run_timeout_live", "run_scenario")
        )
        for restoration_fails in ("yes", "no"):
            with self.subTest(restoration_fails=restoration_fails), tempfile.TemporaryDirectory() as directory:
                script = functions + r'''
set -uo pipefail
runtime_taint=""; preflight_failure=""; lane=self-test; receipt_root="$PWD"
scenario_state_reset() { scenario_name="$1"; scenario_finding=""; scenario_cleanup_failure=""; }
run_timeout_case() { scenario_finding='original assertion failure'; return 17; }
compose() { [[ "$TEST_RESTORATION_FAILS" != yes ]]; }
wait_ready() { return 0; }
wait_peer_ready() { return 0; }
write_receipt() { printf '%s\n%s\n' "$2" "$3" > "$receipt_root/$1.json"; }
follow_up() { echo executed > follow-up-executed; }
cleanup() { echo executed > cleanup-executed; }
run_scenario timeout run_timeout_live cooperative && exit 71
run_scenario next follow_up || true
run_scenario cleanup cleanup || exit 72
[[ -e cleanup-executed ]] || exit 73
if [[ "$TEST_RESTORATION_FAILS" == yes ]]; then
  [[ ! -e follow-up-executed ]] || exit 74
  grep -q 'not executed: timeout qualification topology restoration failed' next.json || exit 75
else
  [[ -e follow-up-executed && -z "$runtime_taint" ]] || exit 76
fi
'''
                completed = subprocess.run(
                    ["bash", "-c", script], cwd=directory,
                    env={**os.environ, "TEST_RESTORATION_FAILS": restoration_fails},
                    capture_output=True, text=True, check=False,
                )
                self.assertEqual(0, completed.returncode, completed.stderr)

    def test_timeout_failures_restore_environment_and_topology_before_follow_up(self):
        source = HARNESS.read_text(encoding="utf-8")
        functions = "\n".join(
            re.search(rf"^{name}\(\) \{{\n.*?^\}}", source, re.M | re.S).group(0)
            for name in ("run_timeout_live", "run_timeout_case", "scenario_fail")
        )
        for initial in ("unset", "custom"):
            for failure in ("setup", "readiness", "submit", "barrier"):
                with self.subTest(initial=initial, failure=failure), tempfile.TemporaryDirectory() as directory:
                    script = functions + r'''
set -uo pipefail
scenario_finding=""; scenario_cleanup_failure=""; scenario_name=timeout
native_payload='{}'
unset HONUA_GP_TIMEOUT_SECONDS HONUA_GP_QUALIFICATION_BARRIER_ROOT HONUA_GP_QUALIFICATION_EXECUTOR_MODE
if [[ "$TEST_INITIAL" == custom ]]; then
  export HONUA_GP_TIMEOUT_SECONDS=7199 HONUA_GP_QUALIFICATION_BARRIER_ROOT=/original HONUA_GP_QUALIFICATION_EXECUTOR_MODE=original
fi
environment_state() {
  printf '%s\n' "${HONUA_GP_TIMEOUT_SECONDS+x}:${HONUA_GP_TIMEOUT_SECONDS-}" \
    "${HONUA_GP_QUALIFICATION_BARRIER_ROOT+x}:${HONUA_GP_QUALIFICATION_BARRIER_ROOT-}" \
    "${HONUA_GP_QUALIFICATION_EXECUTOR_MODE+x}:${HONUA_GP_QUALIFICATION_EXECUTOR_MODE-}"
}
environment_state > before
compose() {
  if [[ "${HONUA_GP_TIMEOUT_SECONDS-}" == 2 ]]; then
    [[ "$TEST_FAILURE" != setup ]] || return 41
  else
    environment_state > restored
  fi
}
wait_ready() {
  [[ "${HONUA_GP_TIMEOUT_SECONDS-}" != 2 || "$TEST_FAILURE" != readiness ]] || return 42
}
wait_peer_ready() { environment_state >> peer-ready; }
now() { echo timestamp; }
submit_async() { [[ "$TEST_FAILURE" != submit ]] || return 43; echo job-1; }
object_file_count() { echo 0; }
jq() { return 0; }
wait_barrier() { scenario_fail 'native barrier failed'; }
write_receipt() { echo unexpected-pass > receipt; }
status=0
run_timeout_live ignore-cancellation || status=$?
[[ "$status" != 0 ]] || exit 51
[[ ! -e receipt ]] || exit 52
environment_state > after
cmp before after && cmp before restored || exit 53
# Both front doors must be ready in the restored environment.
tail -n 3 peer-ready > last-peer-ready
cmp before last-peer-ready || exit 54
[[ "$TEST_FAILURE" != barrier || "$scenario_finding" == 'native barrier failed' ]] || exit 55
'''
                    completed = subprocess.run(
                        ["bash", "-c", script], cwd=directory,
                        env={**os.environ, "TEST_INITIAL": initial, "TEST_FAILURE": failure},
                        capture_output=True, text=True, check=False,
                    )
                    self.assertEqual(0, completed.returncode, completed.stderr)

    def test_timeout_restoration_failure_prevents_a_pass_receipt(self):
        source = HARNESS.read_text(encoding="utf-8")
        wrapper = re.search(r"^run_timeout_live\(\) \{\n.*?^\}", source, re.M | re.S).group(0)
        for failure in ("compose", "ready", "peer"):
            for case_status in (0, 17):
                with self.subTest(failure=failure, case_status=case_status):
                    script = wrapper + r'''
set -uo pipefail
scenario_name=timeout; scenario_finding=""; scenario_cleanup_failure=""
run_timeout_case() {
  job=job-1; state=failed
  if (( TEST_CASE_STATUS != 0 )); then scenario_finding='original failure'; fi
  return "$TEST_CASE_STATUS"
}
compose() { [[ "$TEST_FAILURE" != compose ]]; }
wait_ready() { [[ "$TEST_FAILURE" != ready ]]; }
wait_peer_ready() { [[ "$TEST_FAILURE" != peer ]]; }
write_receipt() { echo unexpected-pass; }
status=0
run_timeout_live cooperative || status=$?
[[ "$status" != 0 && "$scenario_cleanup_failure" == *restoration* ]] || exit 61
[[ "$TEST_CASE_STATUS" == 0 || "$scenario_finding" == 'original failure;'* ]] || exit 62
'''
                    completed = subprocess.run(
                        ["bash", "-c", script],
                        env={**os.environ, "TEST_FAILURE": failure, "TEST_CASE_STATUS": str(case_status)},
                        capture_output=True, text=True, check=False,
                    )
                    self.assertEqual(0, completed.returncode, completed.stderr)
                    self.assertNotIn("unexpected-pass", completed.stdout)

    def test_receipt_serialization_failure_is_reported_as_missing_evidence(self):
        with tempfile.TemporaryDirectory(prefix="gp-receipt-failure-") as directory:
            fake_jq = Path(directory) / "jq"
            fake_jq.write_text(
                '#!/bin/sh\nif [ "$1" = "-n" ] && [ "$2" = "--slurpfile" ]; then exit 24; fi\n'
                f'exec "{shutil.which("jq")}" "$@"\n',
                encoding="utf-8",
            )
            fake_jq.chmod(0o755)
            completed, receipts, summary = self.run_harness(
                "self-test", PATH=f"{directory}:{os.environ['PATH']}"
            )
        self.assertNotEqual(0, completed.returncode)
        self.assertEqual({}, receipts)
        self.assertEqual(0, summary["receipt_count"])
        self.assertEqual(0, summary["passed"])
        self.assertEqual(summary["declared_scenarios"], summary["missing_scenarios"])

    def test_output_store_preflight_never_counts_unexecuted_proof_as_passed(self):
        completed, receipts, summary = self.run_harness(
            "output-store", HONUA_SERVER_IMAGE="unattested:latest"
        )
        self.assertNotEqual(0, completed.returncode)
        self.assertEqual(["topology", "output-store-attestation", "cleanup"], summary["declared_scenarios"])
        self.assertEqual(3, summary["receipt_count"])
        self.assertEqual("fail", receipts["output-store-attestation"]["outcome"])
        self.assertIn("preflight failure", receipts["output-store-attestation"]["finding"])

    def test_dr_preflight_accounts_for_every_required_receipt(self):
        completed, receipts, summary = self.run_harness(
            "output-store-dr", HONUA_SERVER_IMAGE="unattested:latest"
        )
        self.assertNotEqual(0, completed.returncode)
        self.assertEqual(
            ["topology", "output-store-attestation", "output-store-dr", "cleanup"],
            summary["declared_scenarios"],
        )
        self.assertEqual("fail", receipts["output-store-dr"]["outcome"])
        self.assertEqual([], summary["missing_scenarios"])

    def test_crash_preflight_never_qualifies_unexecuted_boundaries(self):
        completed, receipts, summary = self.run_harness(
            "crash-boundaries", HONUA_SERVER_IMAGE="unattested:latest"
        )
        self.assertNotEqual(0, completed.returncode)
        self.assertEqual(8, summary["declared_scenario_count"])
        self.assertEqual(8, summary["receipt_count"])
        self.assertEqual([], summary["missing_scenarios"])
        self.assertEqual(6, sum(name.startswith("crash-") for name in receipts))
        for name, receipt in receipts.items():
            if name.startswith("crash-"):
                self.assertEqual("fail", receipt["outcome"])

    def test_result_semantics_follow_submitted_process_across_subshells(self):
        source = HARNESS.read_text(encoding="utf-8")
        functions = "\n".join(
            re.search(rf"^{name}\(\) \{{\n.*?^\}}", source, re.M | re.S).group(0)
            for name in (
                "record_job_process", "job_process_of", "submit_async",
                "verify_buffer_semantics", "result_digest",
            )
        )
        point = {"type": "Point", "coordinates": [-122.4194, 37.7749]}
        ring = [
            [-122.4194 + math.cos(i * math.pi / 4), 37.7749 + math.sin(i * math.pi / 4)]
            for i in range(8)
        ]
        ring.append(ring[0])
        polygon = {"type": "Polygon", "coordinates": [ring]}
        cases = [
            ("gdal.ogr2ogr", scenario, point, 0, "not-applicable-gdal.ogr2ogr")
            for scenario in ("duplicate-delivery", "retry", "stale-lease", "restart-worker-results-read")
        ] + [
            ("geometry.buffer", "async", polygon, 0, "verified"),
            ("geometry.buffer", "native-looking-name", point, 1, "contains no Polygon"),
        ]
        for process, scenario, geometry, expected_code, semantics in cases:
            with self.subTest(process=process, scenario=scenario), tempfile.TemporaryDirectory() as directory:
                scratch = Path(directory)
                (scratch / "result.json").write_text(json.dumps(geometry), encoding="utf-8")
                script = functions + r'''
set -uo pipefail
job_process_root="$PWD"
scenario_state_file="$PWD/state.json"
payload='{}'
base_url=http://unused
auth_curl() {
  if [[ "${*: -1}" == */execution ]]; then
    printf '{"jobID":"job-1"}'
  else
    cat "$PWD/result.json"
  fi
}
job="$(submit_async "$TEST_PROCESS" '{}')" || exit 2
result_digest "$job"
'''
                completed = subprocess.run(
                    ["bash", "-c", script], cwd=scratch,
                    env={**os.environ, "TEST_PROCESS": process, "scenario_name": scenario,
                         "HONUA_GP_VERIFY_BUFFER_SEMANTICS": "1"},
                    capture_output=True, text=True, check=False,
                )
                self.assertEqual(expected_code, completed.returncode, completed.stderr)
                receipt = json.loads((scratch / "state.json").read_text(encoding="utf-8"))
                self.assertEqual(process, receipt["process"])
                self.assertIn(semantics, receipt["output_semantics"])
                self.assertGreater(receipt["bytes"], 0)
                self.assertEqual(64, len(receipt["sha256"]))

    def run_harness(self, lane, **overrides):
        with tempfile.TemporaryDirectory(prefix="gp-qualification-") as directory:
            environment = os.environ.copy()
            environment.update(
                {
                    "HONUA_GP_LANE": lane,
                    "HONUA_GP_RECEIPT_DIR": directory,
                    "GITHUB_SERVER_URL": "https://github.com",
                    "GITHUB_REPOSITORY": "honua-io/honua-server",
                    "GITHUB_RUN_ID": "3848",
                    "GITHUB_RUN_ATTEMPT": "1",
                    **overrides,
                }
            )
            completed = subprocess.run(
                [str(HARNESS)],
                cwd=ROOT,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )
            receipts = {
                path.stem: json.loads(path.read_text(encoding="utf-8"))
                for path in Path(directory).glob("*.json")
                if path.name != "summary.json" and not path.name.startswith(".")
            }
            summary = json.loads((Path(directory) / "summary.json").read_text(encoding="utf-8"))
            return completed, receipts, summary

    def test_executed_assertion_failure_isolated_from_follow_up(self):
        completed, receipts, summary = self.run_harness("self-test")

        self.assertEqual(1, completed.returncode, completed.stderr)
        self.assertEqual(["assertion-failure", "follow-up", "cleanup"], summary["declared_scenarios"])
        self.assertEqual(3, summary["declared_scenario_count"])
        self.assertEqual(3, summary["receipt_count"])
        self.assertEqual([], summary["missing_scenarios"])
        self.assertEqual([], summary["duplicate_receipts"])
        self.assertEqual("fail", receipts["assertion-failure"]["outcome"])
        self.assertEqual("intentional assertion failure", receipts["assertion-failure"]["finding"])
        self.assertEqual("pass", receipts["follow-up"]["outcome"])
        self.assertIsNone(receipts["follow-up"]["finding"])
        self.assertEqual("x" * 262144, receipts["follow-up"]["evidence"]["payload"])
        self.assertEqual(
            "x" * 262144,
            next(item for item in summary["scenarios"] if item["scenario"] == "follow-up")["evidence"]["payload"],
        )
        for receipt in receipts.values():
            self.assertLessEqual(receipt["started_at"], receipt["completed_at"])
            self.assertIn("attempt_count", receipt)
            self.assertIn("state_transitions", receipt)
            self.assertIn("disruptions", receipt)
            self.assertIn("bytes", receipt["output"])
            self.assertIn("sha256", receipt["output"])
            self.assertEqual(
                "https://github.com/honua-io/honua-server/actions/runs/3848",
                receipt["github"]["run_url"],
            )

    def test_resilience_preflight_emits_topology_and_unrun_receipts(self):
        completed, receipts, summary = self.run_harness(
            "resilience",
            HONUA_SERVER_IMAGE="ghcr.io/honua/server@sha256:" + "a" * 64,
            HONUA_WORKER_IMAGE="ghcr.io/honua/worker@sha256:" + "b" * 64,
            HONUA_GP_SOURCE_SHA="c" * 40,
            HONUA_GP_SKIP_PULL="true",
        )

        self.assertNotEqual(0, completed.returncode)
        self.assertEqual("fail", receipts["topology"]["outcome"])
        self.assertIn("required for resilience qualification", receipts["topology"]["finding"])
        self.assertEqual(summary["declared_scenario_count"], summary["receipt_count"])
        self.assertEqual([], summary["missing_scenarios"])
        self.assertEqual([], summary["duplicate_receipts"])
        self.assertTrue(all(item["outcome"] == "fail" for item in receipts.values() if item["scenario"] != "cleanup"))

    def test_missing_scheduled_canary_receipt_is_a_failed_current_streak_entry(self):
        with tempfile.TemporaryDirectory(prefix="gp-streak-") as directory:
            fake_bin = Path(directory) / "bin"
            fake_bin.mkdir()
            fake_gh = fake_bin / "gh"
            fake_gh.write_text(
                "#!/bin/sh\ncase \"$2\" in *workflows*) echo '{\"workflow_runs\":[]}' ;; "
                "*) echo '{\"id\":9876,\"run_attempt\":1,\"event\":\"schedule\","
                "\"created_at\":\"2026-09-25T00:18:00Z\"}' ;; esac\n",
                encoding="utf-8",
            )
            fake_gh.chmod(0o755)
            streak = Path(directory) / "streak.json"
            environment = os.environ.copy()
            environment.update(
                {
                    "PATH": f"{fake_bin}:{environment['PATH']}",
                    "GITHUB_EVENT_NAME": "schedule",
                    "GITHUB_SERVER_URL": "https://github.com",
                    "GITHUB_REPOSITORY": "honua-io/honua-server",
                    "GITHUB_RUN_ID": "9876",
                    "GITHUB_SHA": "d" * 40,
                    "HONUA_GP_CANARY_STREAK_RECEIPT": str(streak),
                    "HONUA_GP_CANARY_RECEIPT": str(Path(directory) / "missing-receipt.json"),
                }
            )
            completed = subprocess.run(
                [str(STREAK)], cwd=ROOT, env=environment, capture_output=True, text=True, check=False
            )
            self.assertEqual(0, completed.returncode, completed.stderr)
            result = json.loads(streak.read_text(encoding="utf-8"))
            self.assertEqual(1, result["observed_runs"])
            self.assertEqual(0, result["consecutive_green"])
            self.assertFalse(result["ready"])
            self.assertEqual("fail", result["runs"][0]["outcome"])
            self.assertIn("receipt.json", result["runs"][0]["finding"])
            self.assertEqual(9876, result["runs"][0]["run_id"])



class OutputStoreArtifactOracleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location(
            "store_oracle", ROOT / "scripts/qualification/verify-gp-store-artifact.py"
        )
        cls.oracle = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.oracle)

    def fixture(self):
        # The submitted grid uses decimal-degree offsets; the oracle computes
        # expected ordinates independently from integer ten-thousandths.
        return {"type": "FeatureCollection", "features": [
            {"type": "Feature", "properties": {"id": i}, "geometry": {
                "type": "Point", "coordinates": [
                    -157.8583 + (i % 100) / 10000,
                    21.3069 + (i % 50) / 10000,
                ]}}
            for i in range(500)
        ]}

    def test_independent_grid_survives_feature_reordering(self):
        document = self.fixture()
        document["features"].reverse()
        self.oracle.verify(document)

    def test_wrong_values_geometries_and_metadata_are_rejected(self):
        original = self.fixture()
        mutations = {
            "missing feature": lambda d: d["features"].pop(),
            "duplicate id": lambda d: d["features"][1]["properties"].update(id=0),
            "wrong value": lambda d: d["features"][0]["properties"].update(id=501),
            "swapped axes": lambda d: d["features"][0]["geometry"]["coordinates"].reverse(),
            "null ordinate": lambda d: d["features"][0]["geometry"].update(coordinates=[None, 21.3069]),
            "extra ordinate": lambda d: d["features"][0]["geometry"]["coordinates"].append(0),
            "wrong geometry": lambda d: d["features"][0].update(geometry=None),
            "wrong CRS": lambda d: d.update(crs={"type": "name", "properties": {"name": "EPSG:3857"}}),
        }
        for name, mutate in mutations.items():
            with self.subTest(name=name):
                document = copy.deepcopy(original)
                mutate(document)
                with self.assertRaises(ValueError):
                    self.oracle.verify(document)


class NativeObserverTests(unittest.TestCase):
    """Real local processes/files prove observer contracts, never AWS qualification."""

    def setUp(self):
        import sys
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        root = Path(self.temporary.name)
        self.barriers, self.scratch, self.outputs = (root / x for x in ("barriers", "scratch", "outputs"))
        for directory in (self.barriers, self.scratch / "job-1", self.outputs):
            directory.mkdir(parents=True)
        self.policy_path = root / "observer-policy.json"
        self.policy = {"schema": "honua.gp-observer-policy/v1", "enabled": True, "role": "worker",
                       "barrierRoot": str(self.barriers), "scratchRoot": str(self.scratch),
                       "outputRoot": str(self.outputs), "keyPrefix": "gp/outputs",
                       "storeReference": "qualification", "configurationDigest": "a" * 64}
        self.write_policy()
        self.marker = self.outputs / ".honua-gp-store.json"
        self.marker.write_text(json.dumps({"Provider": "local", "PersistenceClass": "shared-persistent",
                                         "StoreReference": "qualification", "ConfigurationDigest": "a" * 64}))
        spec = importlib.util.spec_from_file_location("native_observer", ROOT / "scripts/qualification/gp_native_observer.py")
        self.observer = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.observer)
        self.child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"],
                                      cwd=self.scratch / "job-1", stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.addCleanup(self.stop_child)
        (self.barriers / "job-1").mkdir()
        self.ready_path = self.barriers / "job-1/native-process-started.ready.json"
        self.ready = {"operationId": "job-1", "workerId": "worker-1", "barrier": "native-process-started",
                      "workerProcessId": os.getpid(), "childProcessId": self.child.pid,
                      "readyAt": "2026-10-18T10:00:00Z"}
        self.ready_path.write_text(json.dumps(self.ready))

    def write_policy(self):
        self.policy_path.write_text(json.dumps(self.policy))

    def stop_child(self):
        if self.child.poll() is None:
            self.child.terminate()
        self.child.wait(timeout=5)

    def observe(self, phase="before", expected=None):
        return self.observer.observe(self.policy_path, "job-1", "b" * 32, phase, expected)

    def test_real_child_present_then_absent_with_original_worker_and_empty_effects(self):
        before = self.observe()
        self.assertEqual(self.child.pid, before["process"]["child"]["pid"])
        self.assertGreater(before["process"]["child"]["startTicks"], 0)
        self.stop_child()
        after = self.observe("after", before["process"])
        self.assertTrue(after["process"]["childAbsent"])
        self.assertEqual(before["process"]["worker"], after["process"]["worker"])
        self.assertEqual([], after["outputInventory"]["files"])
        self.assertFalse(after["qualification"])

    def test_live_child_never_becomes_cessation(self):
        before = self.observe()
        with self.assertRaisesRegex(self.observer.Refused, "native-child-still-present"):
            self.observe("after", before["process"])

    def test_stale_worker_namespace_barrier_and_pid_are_refused(self):
        before = self.observe()
        self.stop_child()
        for field, replacement in (("bootId", "wrong"), ("pidNamespace", -1), ("barrierSha256", "0" * 64),
                                   ("workerId", "other"), ("worker", {"pid": os.getpid(), "startTicks": 1})):
            with self.subTest(field=field):
                identity = copy.deepcopy(before["process"])
                identity[field] = replacement
                with self.assertRaises(self.observer.Refused):
                    self.observe("after", identity)
        identity = copy.deepcopy(before["process"])
        identity["child"]["pid"] += 10000
        with self.assertRaisesRegex(self.observer.Refused, "child-identity-changed"):
            self.observe("after", identity)

    def test_wrong_job_or_parent_cannot_supply_native_identity(self):
        for key, replacement in (("operationId", "job-2"), ("workerProcessId", 1), ("childProcessId", os.getpid())):
            with self.subTest(key=key):
                changed = {**self.ready, key: replacement}
                self.ready_path.write_text(json.dumps(changed))
                with self.assertRaises(self.observer.Refused):
                    self.observe()

    def test_physical_bytes_and_changes_are_observed_without_exposing_contents(self):
        import hashlib
        folder = self.outputs / "gp/outputs/job-1"
        folder.mkdir(parents=True)
        (folder / "result").write_bytes(b"first")
        first = self.observe()["outputInventory"]
        self.assertEqual(hashlib.sha256(b"first").hexdigest(), first["files"][0]["sha256"])
        (folder / "result").write_bytes(b"changed")
        second = self.observe()["outputInventory"]
        self.assertNotEqual(first, second)
        self.assertNotIn("changed", json.dumps(second))

    def test_symlink_and_special_file_inventory_fail_closed(self):
        folder = self.outputs / "gp/outputs/job-1"
        folder.mkdir(parents=True)
        link = folder / "secret-link"
        link.symlink_to(self.policy_path)
        with self.assertRaisesRegex(self.observer.Refused, "inventory-symlink"):
            self.observe()
        link.unlink()
        os.mkfifo(folder / "pipe")
        with self.assertRaisesRegex(self.observer.Refused, "inventory-special-file"):
            self.observe()

    def test_disabled_missing_and_wrong_store_are_refused(self):
        self.policy["enabled"] = False
        self.write_policy()
        with self.assertRaisesRegex(self.observer.Refused, "qualification-disabled"):
            self.observe()
        self.policy["enabled"] = True
        self.write_policy()
        self.marker.write_text('{"Provider":"local","StoreReference":"wrong"}')
        with self.assertRaisesRegex(self.observer.Refused, "store-attestation"):
            self.observe()
        self.marker.unlink()
        with self.assertRaises(OSError):
            self.observe()

    def test_store_observer_has_only_read_only_output_scope(self):
        self.policy.update(role="store-observer", barrierRoot=None, scratchRoot=None)
        self.write_policy()
        self.assertEqual("observed", self.observe("store")["status"])
        with self.assertRaisesRegex(self.observer.Refused, "barrier-policy-required"):
            self.observe()

    def test_independent_shared_barrier_proves_entry_without_claiming_live_pid(self):
        self.policy.update(role="store-observer", scratchRoot=None)
        self.write_policy()
        started = self.observe("started")
        self.assertEqual(self.child.pid, started["barrier"]["childProcessId"])
        self.assertNotIn("process", started)
        self.stop_child()
        # The record remains entry evidence even when the task/process stopped;
        # the caller must independently observe the Batch task before cancel.
        self.assertEqual(started["barrier"], self.observe("started")["barrier"])
        with self.assertRaisesRegex(self.observer.Refused, "worker-policy-required"):
            self.observe()

    def test_path_traversal_and_unbounded_inventory_are_refused(self):
        for job in ("..", "../job-1", "job/1", "job%2F1"):
            with self.subTest(job=job), self.assertRaises(self.observer.Refused):
                self.observer.observe(self.policy_path, job, "b" * 32, "before")
        folder = self.outputs / "gp/outputs/job-1"
        folder.mkdir(parents=True)
        with (folder / "large").open("wb") as stream:
            stream.truncate(self.observer.MAX_BYTES + 1)
        with self.assertRaisesRegex(self.observer.Refused, "inventory-limit"):
            self.observe()


if __name__ == "__main__":
    unittest.main()
