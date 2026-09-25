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
                "#!/bin/sh\nprintf '%s\\n' '[{\"databaseId\":1,\"conclusion\":\"success\",\"createdAt\":\"2026-09-02T00:00:00Z\",\"headSha\":\"a\",\"url\":\"u1\"},{\"databaseId\":2,\"conclusion\":\"success\",\"createdAt\":\"2026-09-01T00:00:00Z\",\"headSha\":\"b\",\"url\":\"u2\"}]'\n",
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
            self.assertEqual(3, result["observed_runs"])
            self.assertEqual(0, result["consecutive_green"])
            self.assertFalse(result["ready"])
            self.assertEqual("failure", result["runs"][0]["conclusion"])
            self.assertTrue(result["runs"][0]["missing_receipt"])
            self.assertEqual(
                "https://github.com/honua-io/honua-server/actions/runs/9876",
                result["runs"][0]["url"],
            )


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


if __name__ == "__main__":
    unittest.main()
