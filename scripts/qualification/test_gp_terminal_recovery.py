"""Reject false terminal-crash evidence; mocks are not qualification receipts."""

import copy
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path

from test_gp_store_crash import functions

ROOT = Path(__file__).resolve().parents[2]
RECOVERY = ROOT / "scripts/qualification/gp-terminal-recovery.sh"


class TerminalRecoveryTests(unittest.TestCase):
    def run_shell(self, script, **environment):
        with tempfile.TemporaryDirectory() as directory:
            result = subprocess.run(["bash", "-c", script], cwd=directory,
                                    env={**os.environ, **environment}, capture_output=True,
                                    text=True, check=False)
            self.assertEqual(0, result.returncode, result.stderr + result.stdout)

    def test_fence_requires_matching_durable_success_and_withheld_success_reply(self):
        terminal = {"operationId": "job-1", "status": 3, "claimedBy": "worker-1",
                    "version": 5, "artifactReferences": ["descriptor"]}
        fence = {"operationId": "job-1", "workerId": "worker-1",
                 "barrier": "terminal-committed-registration-pending", "redis_reply": ":1",
                 "reply_forwarded": False, "terminal_record": terminal}
        variants = [({}, True), ({"reply_forwarded": True}, False), ({"redis_reply": ":0"}, False),
                    ({"operationId": "other"}, False), ({"workerId": "other"}, False),
                    ({"terminal_record": {**terminal, "version": 6}}, False),
                    ({"terminal_record": {**terminal, "status": 2}}, False)]
        for mutation, expected in variants:
            with self.subTest(mutation=mutation):
                script = functions(RECOVERY, "terminal_assert_fence") + r'''
job=job-1; before_record="$TERMINAL"; ready="$FENCE"
result=0; terminal_assert_fence || result=$?
if [[ "$EXPECTED" == pass ]]; then [[ "$result" == 0 ]]; else [[ "$result" != 0 ]]; fi
'''
                self.run_shell(script, TERMINAL=json.dumps(terminal), FENCE=json.dumps({**fence, **mutation}),
                               EXPECTED="pass" if expected else "fail")

    def test_worker_death_requires_sigkill_not_stop_or_oom(self):
        for running, exit_code, oom, expected in (
                (False, 137, False, True), (True, 137, False, False),
                (False, 0, False, False), (False, 143, False, False), (False, 137, True, False)):
            with self.subTest(running=running, exit_code=exit_code, oom=oom):
                script = functions(RECOVERY, "terminal_assert_killed") + r'''
printf '%s' "$INSPECT" > inspect.json
result=0; terminal_assert_killed inspect.json || result=$?
if [[ "$EXPECTED" == pass ]]; then [[ "$result" == 0 ]]; else [[ "$result" != 0 ]]; fi
'''
                self.run_shell(script, INSPECT=json.dumps([{"State": {"Running": running,
                               "ExitCode": exit_code, "OOMKilled": oom}}]),
                               EXPECTED="pass" if expected else "fail")

    def test_record_comparison_rejects_metadata_changes_and_invalid_json(self):
        baseline = {"version": 5, "attemptCount": 1, "currentPhase": "Completed",
                    "warnings": [], "percentComplete": 100, "spec": {"operation": "original"}}
        variants = [(json.dumps(baseline), json.dumps(dict(reversed(list(baseline.items())))), True)]
        for mutation in ({"version": 6}, {"attemptCount": 2}, {"warnings": ["late"]},
                         {"percentComplete": 50}, {"spec": {"operation": "changed"}}):
            variants.append((json.dumps(baseline), json.dumps({**baseline, **mutation}), False))
        variants.extend([("invalid", "invalid", False), ("", "", False), ("null", "null", False)])
        for before, after, expected in variants:
            with self.subTest(after=after):
                script = functions(RECOVERY, "terminal_assert_same_record") + r'''
printf '%s' "$BEFORE" > before.json; printf '%s' "$AFTER" > after.json
result=0; terminal_assert_same_record before.json after.json || result=$?
if [[ "$EXPECTED" == pass ]]; then [[ "$result" == 0 ]]; else [[ "$result" != 0 ]]; fi
'''
                self.run_shell(script, BEFORE=before, AFTER=after, EXPECTED="pass" if expected else "fail")

    @staticmethod
    def package_fixture():
        route = "/api/geoprocessing/jobs/job-1/artifacts/0/content"
        descriptor = {"outputType": "staged-object", "jobId": "job-1", "attemptNumber": 1,
                      "content": {"sizeBytes": 52840, "mediaType": "application/geo+json",
                                  "checksum": {"algorithm": "sha256", "value": "fixture-hash"}}}
        terminal = {"operationId": "job-1", "version": 5, "attemptCount": 1,
                    "artifactReferences": [json.dumps(descriptor)]}
        artifact = {"artifactId": "job-1:artifact:1", "uri": route, "contentType": "application/geo+json",
                    "metadata": {"raster.output.contentRoute": route, "raster.output.staged": "true",
                                 "raster.output.sizeBytes": "52840", "raster.output.checksum": "sha256:fixture-hash"}}
        return terminal, {"resultPackageId": "job-1:v5", "status": 5, "artifacts": [artifact]}

    def test_result_package_binds_status_route_and_committed_content_metadata(self):
        terminal, package = self.package_fixture()
        variants = [(package, True)]
        for key, value in (("resultPackageId", "job-1:v4"), ("status", 6), ("artifacts", []),
                           ("artifacts", package["artifacts"] * 2)):
            variants.append(({**package, key: value}, False))
        for uri in ("/api/geoprocessing/jobs/other/artifacts/0/content",
                    "/api/geoprocessing/jobs/job-1/artifacts/1/content", "//evil.example/content",
                    "https://evil.example/content", package["artifacts"][0]["uri"] + "?other=1"):
            candidate = copy.deepcopy(package)
            candidate["artifacts"][0]["uri"] = uri
            variants.append((candidate, False))
        for key, value in (("raster.output.sizeBytes", "1"), ("raster.output.checksum", "sha256:wrong"),
                           ("raster.output.contentRoute", "/other"), ("raster.output.staged", "false")):
            candidate = copy.deepcopy(package)
            candidate["artifacts"][0]["metadata"][key] = value
            variants.append((candidate, False))
        for candidate, expected in variants:
            with self.subTest(package=candidate):
                script = functions(RECOVERY, "terminal_assert_package") + r'''
before_record="$TERMINAL"
printf '%s' "$PACKAGE" > package.json
result=0; terminal_assert_package package.json || result=$?
if [[ "$EXPECTED" == pass ]]; then [[ "$result" == 0 ]]; else [[ "$result" != 0 ]]; fi
'''
                self.run_shell(script, TERMINAL=json.dumps(terminal), PACKAGE=json.dumps(candidate),
                               EXPECTED="pass" if expected else "fail")

    def test_public_value_result_matches_committed_geojson_without_other_envelopes(self):
        _, package = self.package_fixture()
        committed = {"type": "FeatureCollection", "features": [{"type": "Feature",
                     "properties": {"id": 1}, "geometry": {"type": "Point", "coordinates": [1, 2]}}]}
        output = {"value": committed, "mediaType": package["artifacts"][0]["contentType"]}
        variants = [({"outputFeatureLayer": output}, True),
                    ({"outputs": {"outputFeatureLayer": output}}, False),
                    ({"outputFeatureLayer": {"href": "http://fixture/output", "type": "application/geo+json"}}, False),
                    ({"outputFeatureLayer": {**output, "mediaType": "text/html"}}, False),
                    ({"outputFeatureLayer": {**output, "value": {"type": "FeatureCollection", "features": []}}}, False),
                    ({"outputFeatureLayer": {**output, "unexpected": True}}, False),
                    ({"outputFeatureLayer": output, "other": output}, False)]
        for result, expected in variants:
            with self.subTest(result=result):
                script = functions(RECOVERY, "terminal_assert_public_result") + r'''
printf '%s' "$PACKAGE" > package.json; printf '%s' "$RESULT" > result.json
printf '%s' "$COMMITTED" > committed.geojson
result=0; terminal_assert_public_result result.json package.json committed.geojson || result=$?
if [[ "$EXPECTED" == pass ]]; then [[ "$result" == 0 ]]; else [[ "$result" != 0 ]]; fi
'''
                self.run_shell(script, PACKAGE=json.dumps(package), RESULT=json.dumps(result),
                               COMMITTED=json.dumps(committed), EXPECTED="pass" if expected else "fail")


if __name__ == "__main__":
    unittest.main()
