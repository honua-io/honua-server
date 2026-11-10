#!/usr/bin/env python3
"""Keep configurable compute routing separate from hosted control jobs and OS pins."""
from dataclasses import dataclass
import os
from pathlib import Path
import subprocess
import tempfile
import time
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[3]
HEAVY_JOBS = {
    "build", "format", "dotnet-foundation-family-tests", "server-tests", "runtime-proofs",
    "mcp-certification", "mcp-llm-smoke", "postgres-compat",
    "core-package-compatibility", "aot-build", "docker-build", "quickstart-console-smoke",
}
HOSTED_JOBS = {
    "pr-template-check", "pr-readiness", "changes", "targeted-shards",
    "ci-router-validation", "dotnet-foundation-tests", "js-typecheck", "test-all",
    "merge-queue-gate", "ci-gate",
}
PINNED_JOBS = {
    "worker-gdal-tests", "python-integration-tests", "js-integration-tests",
    "esri-leaflet-browser-tests", "maplibre-compat",
}


class HeavyRunnerContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.jobs = yaml.safe_load((ROOT / ".github/workflows/ci.yml").read_text())["jobs"]

    def test_compute_jobs_use_operator_label_with_hosted_fallback(self):
        for name in HEAVY_JOBS:
            with self.subTest(job=name):
                self.assertEqual(self.jobs[name]["runs-on"],
                                 "${{ vars.HONUA_CI_HEAVY_RUNNER || 'ubuntu-latest' }}")

    def test_hosted_jobs_stay_on_ubuntu_latest(self):
        for name in HOSTED_JOBS:
            with self.subTest(job=name):
                self.assertEqual(self.jobs[name]["runs-on"], "ubuntu-latest")

    def test_native_and_browser_jobs_retain_supported_os_pins(self):
        for name in PINNED_JOBS:
            with self.subTest(job=name):
                self.assertEqual(self.jobs[name]["runs-on"], "ubuntu-24.04")

    def test_every_ci_job_has_a_runner_class(self):
        self.assertEqual(set(self.jobs), HEAVY_JOBS | HOSTED_JOBS | PINNED_JOBS)


PR_GATE_SELECTED_JOBS = {"format", "fast-tests", "pr-gate"}
SELECTED_RUNNER = "${{ needs.runner-select.outputs.runner || 'ubuntu-latest' }}"
SELECT_SCRIPT = ROOT / "scripts/ci/select-gate-runner.sh"
AWS_STUB = """#!/usr/bin/env bash
echo "$*" >> "$AWS_STUB_LOG"
[[ -z "${AWS_STUB_FAIL:-}" ]] || exit 1
case "$*" in
  *set-desired-capacity*) ;;
  *DesiredCapacity*) echo "$AWS_STUB_DESIRED" ;;
  *InstanceId*) echo "$AWS_STUB_INSTANCE" ;;
  *get-parameter*) echo "$AWS_STUB_HEARTBEAT" ;;
esac
"""


class PrGateRunnerRoutingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.jobs = yaml.safe_load((ROOT / ".github/workflows/pr-gate.yml").read_text())["jobs"]

    def test_selected_jobs_follow_runner_select_without_a_job_condition(self):
        for name in PR_GATE_SELECTED_JOBS:
            with self.subTest(job=name):
                job = self.jobs[name]
                self.assertEqual(job["runs-on"], SELECTED_RUNNER)
                self.assertEqual(job["needs"], "runner-select")
                self.assertNotIn("if", job)

    def test_selection_and_every_other_job_stay_hosted(self):
        for name in set(self.jobs) - PR_GATE_SELECTED_JOBS:
            with self.subTest(job=name):
                self.assertEqual(self.jobs[name]["runs-on"], "ubuntu-latest")

    def test_selection_cannot_fail_and_is_inert_without_the_variable_or_in_enforce_mode(self):
        select = self.jobs["runner-select"]
        self.assertNotIn("if", select)
        self.assertEqual(select["outputs"]["runner"], "${{ steps.select.outputs.runner || 'ubuntu-latest' }}")
        self.assertEqual(select["permissions"], {"contents": "read", "id-token": "write"})
        for step in select["steps"]:
            with self.subTest(step=step.get("name") or step.get("uses")):
                self.assertTrue(step["if"].startswith("vars.HONUA_PR_GATE_RUNNER != '' && env.REVIEW_FIRST_MODE != 'enforce'"))
                self.assertTrue(step["continue-on-error"])
        self.assertLess(sum(step["timeout-minutes"] for step in select["steps"]), select["timeout-minutes"])


WAKE_CREDENTIALS = {"AWS_ACCESS_KEY_ID": "stub"}
FAILING_AWS = {**WAKE_CREDENTIALS, "AWS_STUB_FAIL": "1"}


@dataclass(frozen=True)
class HostScenario:
    label: str = "honua-build"
    desired: str = "1"
    instance: str = "i-current"
    heartbeat: str = "{}"


def heartbeat(idle, instance="i-current", age=0):
    return f'{{"instance": "{instance}", "at": {int(time.time()) - age}, "idle": {idle}}}'


class GateRunnerSelectionTests(unittest.TestCase):
    def run_selection(self, scenario, environment):
        """Runs the selector against a stubbed aws CLI; returns its output and the aws calls."""
        with tempfile.TemporaryDirectory() as scratch:
            scratch = Path(scratch)
            stub = scratch / "aws"
            stub.write_text(AWS_STUB)
            stub.chmod(0o755)
            output = scratch / "output"
            log = scratch / "aws.log"
            log.touch()
            inherited = {key: value for key, value in os.environ.items()
                         if key not in {"AWS_ACCESS_KEY_ID", "GITHUB_STEP_SUMMARY"}}
            env = {
                **inherited,
                "PATH": f"{scratch}{os.pathsep}{os.environ['PATH']}",
                "GITHUB_OUTPUT": str(output),
                "GATE_RUNNER_WAIT_SECONDS": "0",
                "HONUA_PR_GATE_RUNNER": scenario.label,
                "AWS_STUB_LOG": str(log),
                "AWS_STUB_DESIRED": scenario.desired,
                "AWS_STUB_INSTANCE": scenario.instance,
                "AWS_STUB_HEARTBEAT": scenario.heartbeat,
                **environment,
            }
            subprocess.run(["bash", str(SELECT_SCRIPT)], env=env, check=True, capture_output=True, timeout=30)
            return output.read_text().strip(), log.read_text()

    def selected_runner(self, scenario, environment=WAKE_CREDENTIALS):
        return self.run_selection(scenario, environment)[0]

    def test_unset_label_selects_hosted_without_touching_aws(self):
        self.assertEqual(self.run_selection(HostScenario(label=""), WAKE_CREDENTIALS), ("runner=ubuntu-latest", ""))

    def test_missing_wake_credentials_select_hosted(self):
        self.assertEqual(self.selected_runner(HostScenario(), {}), "runner=ubuntu-latest")

    def test_fresh_heartbeat_with_two_idle_runners_selects_the_label(self):
        output, calls = self.run_selection(HostScenario(heartbeat=heartbeat(idle=2)), WAKE_CREDENTIALS)
        self.assertEqual(output, "runner=honua-build")
        self.assertNotIn("set-desired-capacity", calls)

    def test_busy_host_selects_hosted_without_waiting(self):
        self.assertEqual(self.selected_runner(HostScenario(heartbeat=heartbeat(idle=1))), "runner=ubuntu-latest")

    def test_heartbeat_from_a_replaced_or_silent_host_is_not_ready(self):
        for stale in (heartbeat(idle=2, instance="i-previous"), heartbeat(idle=2, age=600)):
            with self.subTest(heartbeat=stale):
                self.assertEqual(self.selected_runner(HostScenario(heartbeat=stale)), "runner=ubuntu-latest")

    def test_stopped_host_is_woken_and_the_gate_falls_back(self):
        output, calls = self.run_selection(HostScenario(desired="0", instance="None"), WAKE_CREDENTIALS)
        self.assertEqual(output, "runner=ubuntu-latest")
        self.assertIn("set-desired-capacity --auto-scaling-group-name honua-lane-build-host --desired-capacity 1", calls)

    def test_aws_failure_selects_hosted(self):
        self.assertEqual(self.selected_runner(HostScenario(), FAILING_AWS), "runner=ubuntu-latest")

if __name__ == "__main__":
    unittest.main()
