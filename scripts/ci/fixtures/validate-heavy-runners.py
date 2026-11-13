#!/usr/bin/env python3
"""Keep configurable compute routing separate from hosted control jobs and OS pins."""
from dataclasses import dataclass
from itertools import product
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[3]
HEAVY_JOBS = {
    "format", "dotnet-foundation-family-tests", "runtime-proofs",
    "mcp-certification", "mcp-llm-smoke", "postgres-compat",
    "core-package-compatibility", "aot-build", "docker-build", "quickstart-console-smoke",
}
HOSTED_JOBS = {
    "pr-template-check", "pr-readiness", "supersede-trailing-runs", "changes", "targeted-shards",
    "producer-runner", "ci-router-validation", "dotnet-foundation-tests", "js-typecheck", "test-all",
    "merge-queue-gate", "ci-gate", "doctor-notify",
}
REUSABLE_JOBS = {"store-image-proofs": "./.github/workflows/store-image-size.yml"}
PINNED_JOBS = {
    "worker-gdal-tests", "python-integration-tests", "js-integration-tests",
    "esri-leaflet-browser-tests", "maplibre-compat",
}


# Even matrix indices of the server test shards; forks never reach self-hosted compute.
EVEN_INDEX = " || ".join(f"endsWith(format('{{0}}', strategy.job-index), '{digit}')" for digit in "02468")
HEAVY_SHARD_RUNNER = (
    "vars.HONUA_HEAVY_RUNNER && (" + EVEN_INDEX + ")"
    " && (!github.event.pull_request || github.event.pull_request.head.repo.full_name == github.repository)"
    " && (!github.event.workflow_run || github.event.workflow_run.head_repository.full_name == github.repository)"
    " && fromJSON(vars.HONUA_HEAVY_RUNNER)"
)
SHARD_JOBS = {
    "ci.yml": ("server-tests", "${{ " + HEAVY_SHARD_RUNNER + " || vars.HONUA_CI_HEAVY_RUNNER || 'ubuntu-latest' }}"),
    "pr-gate.yml": ("affected-shard", "${{ needs.pr-gate.outputs.heavy_ready == 'true' && " + HEAVY_SHARD_RUNNER + " || 'ubuntu-latest' }}"),
}


class ShardRunnerRoutingTests(unittest.TestCase):
    def shard_job(self, workflow):
        name, _ = SHARD_JOBS[workflow]
        return yaml.safe_load((ROOT / ".github/workflows" / workflow).read_text())["jobs"][name]

    def test_even_shards_use_the_fork_guarded_heavy_pool_with_hosted_fallback(self):
        for workflow, (_, runs_on) in SHARD_JOBS.items():
            with self.subTest(workflow=workflow):
                self.assertEqual(self.shard_job(workflow)["runs-on"], runs_on)

    def test_postgres_uses_an_ephemeral_host_port_that_the_tests_receive(self):
        for workflow in SHARD_JOBS:
            with self.subTest(workflow=workflow):
                job = self.shard_job(workflow)
                self.assertEqual(job["services"]["postgres"]["ports"], [5432])
                self.assertNotIn("HONUA_TEST_DB_URL", job["env"])
                urls = [step["env"]["HONUA_TEST_DB_URL"] for step in job["steps"]
                        if "HONUA_TEST_DB_URL" in step.get("env", {})]
                self.assertEqual(len(urls), 1)
                self.assertIn("Port={2}", urls[0])
                self.assertIn("job.services.postgres.ports['5432']", urls[0])


class ServicePortIsolationTests(unittest.TestCase):
    def workflow_postgres_jobs(self, workflow):
        jobs = yaml.safe_load((ROOT / ".github/workflows" / workflow).read_text())["jobs"]
        for name, job in jobs.items():
            if "postgres" in job.get("services", {}):
                yield workflow, name, job

    def postgres_jobs(self):
        for workflow in SHARD_JOBS:
            yield from self.workflow_postgres_jobs(workflow)

    def assert_assigned_port(self, workflow, name, job):
        self.assertEqual(job["services"]["postgres"]["ports"], [5432])
        if name == SHARD_JOBS[workflow][0]:
            return  # The shard-specific connection contract is checked above.
        steps = job["steps"]
        configure = [step for step in steps
                     if step.get("uses") == "./.github/actions/configure-postgres-connection"]
        self.assertEqual(len(configure), 1)
        self.assertEqual(configure[0]["with"]["port"], "${{ job.services.postgres.ports['5432'] }}")
        checkout = next(step for step in steps if step.get("uses", "").startswith("actions/checkout@"))
        self.assertLess(steps.index(checkout), steps.index(configure[0]))
        for step in steps:
            self.assertNotIn("HONUA_TEST_DB_URL", step.get("env", {}))

    def test_every_postgres_service_uses_an_assigned_host_port(self):
        for workflow, name, job in self.postgres_jobs():
            with self.subTest(workflow=workflow, job=name):
                self.assert_assigned_port(workflow, name, job)

    def test_connection_action_exports_both_consumers(self):
        action = yaml.safe_load((ROOT / ".github/actions/configure-postgres-connection/action.yml").read_text())
        step, = action["runs"]["steps"]
        self.assertEqual(step["env"]["PGPORT"], "${{ inputs.port }}")
        self.assertIn('PGPORT=$PGPORT', step["run"])
        self.assertIn('HONUA_TEST_DB_URL=Host=localhost;Port=$PGPORT;', step["run"])

    def test_integration_server_and_docker_smoke_receive_the_assigned_port(self):
        action = yaml.safe_load((ROOT / ".github/actions/setup-honua-server/action.yml").read_text())
        connections = [step["env"]["ConnectionStrings__DefaultConnection"]
                       for step in action["runs"]["steps"]
                       if "ConnectionStrings__DefaultConnection" in step.get("env", {})]
        self.assertEqual(len(connections), 1)
        self.assertIn("Port=${{ env.PGPORT || '5432' }};", connections[0])
        jobs = yaml.safe_load((ROOT / ".github/workflows/ci.yml").read_text())["jobs"]
        smoke = next(step for step in jobs["docker-build"]["steps"] if step.get("name") == "Run Docker container")
        self.assertIn('Port=${PGPORT};', smoke["run"])


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

    def test_reusable_jobs_delegate_runner_selection(self):
        for name, workflow in REUSABLE_JOBS.items():
            with self.subTest(job=name):
                self.assertEqual(self.jobs[name]["uses"], workflow)
                self.assertNotIn("runs-on", self.jobs[name])

    def test_every_ci_job_has_a_runner_class(self):
        self.assertEqual(set(self.jobs), HEAVY_JOBS | HOSTED_JOBS | PINNED_JOBS | set(REUSABLE_JOBS) | {"build", SHARD_JOBS["ci.yml"][0]})


PR_GATE_SELECTED_JOBS = {"pr-gate", "server-test-producer"}
SELECTED_RUNNER = (
    "${{ (!github.event.pull_request || github.event.pull_request.head.repo.full_name == github.repository)"
    " && needs.runner-select.outputs.runner || 'ubuntu-latest' }}"
)
PR_GATE_LIGHT_JOBS = {"format", "fast-tests", "affected-shards-select", "affected-shards", "ci-router-validation", "required"}
LIGHT_RUNNER = (
    "${{ vars.HONUA_LIGHT_RUNNER"
    " && (!github.event.pull_request || github.event.pull_request.head.repo.full_name == github.repository)"
    " && (!github.event.workflow_run || github.event.workflow_run.head_repository.full_name == github.repository)"
    " && fromJSON(vars.HONUA_LIGHT_RUNNER) || 'ubuntu-latest' }}"
)
READY_ONLY = "github.event_name != 'pull_request' || !github.event.pull_request.draft"
DRAFT_FORMAT_ADMISSION = ("always() && !cancelled() && (needs.runner-select.result == 'success' "
                          "|| github.event.pull_request.draft == true)")
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

    def test_only_producers_follow_runner_select(self):
        for name in PR_GATE_SELECTED_JOBS:
            with self.subTest(job=name):
                job = self.jobs[name]
                self.assertEqual(job["runs-on"], SELECTED_RUNNER)
                self.assertEqual(job["needs"], "runner-select")
                self.assertIsNone(job.get("if"))

    def test_early_checks_never_use_producer_admission_or_static_heavy_labels(self):
        probe = """const {expression, light, fork, selected, draft} = JSON.parse(require('fs').readFileSync(0, 'utf8'));
const github = {repository: 'owner/repo', event: {pull_request: {draft, head: {repo: {full_name: fork ? 'fork/repo' : 'owner/repo'}}}}};
const needs = {'runner-select': {outputs: {runner: selected}}};
const vars = {HONUA_PR_GATE_RUNNER: 'honua-build', HONUA_HEAVY_RUNNER: '{"group":"honua-heavy"}', HONUA_LIGHT_RUNNER: light};
process.stdout.write(JSON.stringify(new Function('context', `const {github, needs, vars, fromJSON} = context; return (${expression});`)({github, needs, vars, fromJSON: JSON.parse})));
"""
        pool = {"group": "honua-light", "labels": ["honua-light"]}
        scenarios = [
            ("", False, False, "ubuntu-latest"),
            ("", False, True, "ubuntu-latest"),
            (json.dumps(pool), False, False, pool),
            (json.dumps(pool), True, False, "ubuntu-latest"),
        ]
        for name, selected, scenario in product(
            ["format", "fast-tests"], ["honua-build", "ubuntu-latest", ""], scenarios,
        ):
            light, fork, draft, expected = scenario
            expression = self.jobs[name]["runs-on"].removeprefix("${{").removesuffix("}}").strip()
            context = {
                "expression": expression.replace("needs.runner-select", "needs['runner-select']"),
                "selected": selected, "light": light, "fork": fork, "draft": draft,
            }
            with self.subTest(job=name, selected=selected, fork=fork, draft=draft):
                self.assertEqual(self.evaluate_routing(probe, context), expected)
        self.assertEqual(self.jobs["format"]["needs"], "runner-select")
        self.assertEqual(self.jobs["fast-tests"]["needs"], "runner-select")
        self.assertEqual(self.jobs["format"]["if"], DRAFT_FORMAT_ADMISSION)
        self.assertNotIn("if", self.jobs["fast-tests"])

    @staticmethod
    def evaluate_routing(probe, context):
        result = subprocess.run(
            ["node", "-e", probe], input=json.dumps(context),
            text=True, capture_output=True, check=True,
        )
        return json.loads(result.stdout)

    def test_light_jobs_use_the_fork_guarded_light_pool_with_hosted_fallback(self):
        for name in PR_GATE_LIGHT_JOBS:
            with self.subTest(job=name):
                self.assertEqual(self.jobs[name]["runs-on"], LIGHT_RUNNER)

    def test_selection_and_every_other_job_stay_hosted(self):
        for name in set(self.jobs) - PR_GATE_SELECTED_JOBS - PR_GATE_LIGHT_JOBS - {SHARD_JOBS["pr-gate.yml"][0]}:
            with self.subTest(job=name):
                self.assertEqual(self.jobs[name]["runs-on"], "ubuntu-latest")

    def test_heavy_proof_flows_through_existing_dependencies(self):
        self.assertEqual(self.jobs["runner-select"]["outputs"]["heavy_ready"],
                         "${{ steps.select.outputs.heavy_ready || 'false' }}")
        self.assertEqual(self.jobs["pr-gate"]["outputs"]["heavy_ready"],
                         "${{ needs.runner-select.outputs.heavy_ready }}")
        self.assertEqual(self.jobs["affected-shard"]["needs"],
                         ["affected-shards-select", "pr-gate", "server-test-producer"])

    def test_only_proven_same_repo_even_shards_use_heavy_runners(self):
        probe = """const {expression, ready, fork, index, heavy} = JSON.parse(require('fs').readFileSync(0, 'utf8'));
const github = {repository: 'owner/repo', event: {pull_request: {head: {repo: {full_name: fork ? 'fork/repo' : 'owner/repo'}}}}};
const needs = {'pr-gate': {outputs: {heavy_ready: ready}}};
const vars = {HONUA_HEAVY_RUNNER: heavy};
const strategy = {'job-index': index};
const format = (_, value) => String(value), endsWith = (value, suffix) => value.endsWith(suffix);
process.stdout.write(JSON.stringify(new Function('context', `const {github, needs, vars, strategy, fromJSON, format, endsWith} = context; return (${expression});`)({github, needs, vars, strategy, fromJSON: JSON.parse, format, endsWith})));
"""
        pool = {"group": "honua-heavy", "labels": ["honua-heavy"]}
        expression = self.jobs["affected-shard"]["runs-on"].removeprefix("${{").removesuffix("}}").strip()
        expression = expression.replace("needs.pr-gate", "needs['pr-gate']").replace("strategy.job-index", "strategy['job-index']")
        for ready, fork, index, configured in product(["true", "false", ""], [False, True], [0, 1, 2], [False, True]):
            context = {"expression": expression, "ready": ready, "fork": fork,
                       "index": index, "heavy": json.dumps(pool) if configured else ""}
            expected = pool if ready == "true" and not fork and index % 2 == 0 and configured else "ubuntu-latest"
            with self.subTest(ready=ready, fork=fork, index=index, configured=configured):
                self.assertEqual(self.evaluate_routing(probe, context), expected)

    def test_selection_cannot_fail_and_is_inert_without_the_variable_or_in_enforce_mode(self):
        select = self.jobs["runner-select"]
        self.assertEqual(select["if"], READY_ONLY)
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
    def test_sparse_selector_checkouts_include_shared_heartbeat_validator(self):
        for workflow, job in (("ci.yml", "producer-runner"), ("pr-gate.yml", "runner-select")):
            jobs = yaml.safe_load((ROOT / ".github/workflows" / workflow).read_text())["jobs"]
            checkout = next(step for step in jobs[job]["steps"] if "sparse-checkout" in step.get("with", {}))
            self.assertIn("scripts/ci/gate-runner-heartbeat.sh", checkout["with"]["sparse-checkout"].splitlines())

    def run_selection_details(self, scenario, environment):
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
            fields = dict(line.split("=", 1) for line in output.read_text().splitlines())
            return fields, log.read_text()

    def run_selection(self, scenario, environment):
        fields, calls = self.run_selection_details(scenario, environment)
        return f"runner={fields['runner']}", calls

    def selected_runner(self, scenario, environment=WAKE_CREDENTIALS):
        return self.run_selection(scenario, environment)[0]

    def test_heavy_proof_is_independent_and_fails_closed(self):
        fresh = json.loads(heartbeat(idle=0))
        pool = {"version": 1, "idle": 2}
        cases = [
            ({"idle": 0, "heavy": pool}, "ubuntu-latest", "true"),
            ({"idle": 0, "heavy": {"version": 1, "idle": 1}}, "ubuntu-latest", "true"),
            ({"idle": 0, "heavy": {"version": 1, "idle": 8}}, "ubuntu-latest", "true"),
            ({"idle": 2, "heavy": {"version": 1, "idle": 0}}, "honua-build", "false"),
            ({"idle": 2}, "honua-build", "false"),
            ({"idle": 2, "heavy": None}, "honua-build", "false"),
            ({"idle": 2, "heavy": []}, "honua-build", "false"),
            ({"idle": 2, "heavy": {"version": 2, "idle": 2}}, "honua-build", "false"),
            ({"idle": 2, "heavy": {"version": 1, "idle": "2"}}, "honua-build", "false"),
            ({"idle": 2, "heavy": {"version": 1, "idle": 1.5}}, "honua-build", "false"),
            ({"idle": 2, "heavy": {"version": 1, "idle": 9}}, "honua-build", "false"),
            ({"instance": "i-old", "heavy": pool}, "ubuntu-latest", "false"),
            ({"at": int(time.time()) - 600, "heavy": pool}, "ubuntu-latest", "false"),
            ({"at": int(time.time()) + 600, "heavy": pool}, "ubuntu-latest", "false"),
            ({"at": "invalid", "heavy": pool}, "ubuntu-latest", "false"),
        ]
        for overrides, runner, ready in cases:
            with self.subTest(overrides=overrides):
                scenario = HostScenario(heartbeat=json.dumps({**fresh, **overrides}))
                fields, _ = self.run_selection_details(scenario, WAKE_CREDENTIALS)
                self.assertEqual(fields, {"runner": runner, "heavy_ready": ready})
        for invalid in ["{", "null", "[]", "{}"]:
            with self.subTest(invalid=invalid):
                fields, _ = self.run_selection_details(HostScenario(heartbeat=invalid), WAKE_CREDENTIALS)
                self.assertEqual(fields, {"runner": "ubuntu-latest", "heavy_ready": "false"})

    def test_multiple_heartbeat_documents_never_admit_either_pool(self):
        fresh = json.loads(heartbeat(idle=2))
        fresh["heavy"] = {"version": 1, "idle": 2}
        proof = json.dumps(fresh)
        for payload in ("{}\n" + proof, proof + "\n{}", proof + "\n" + proof, ""):
            with self.subTest(payload=payload):
                fields, _ = self.run_selection_details(HostScenario(heartbeat=payload), WAKE_CREDENTIALS)
                self.assertEqual(fields, {"runner": "ubuntu-latest", "heavy_ready": "false"})

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


class CiProducerSelectionTests(unittest.TestCase):
    def selection(self, payload, environment=None):
        with tempfile.TemporaryDirectory() as scratch:
            scratch = Path(scratch)
            stub = scratch / "aws"
            stub.write_text(AWS_STUB)
            stub.chmod(0o755)
            log = scratch / "calls"
            log.touch()
            output = scratch / "output"
            env = {key: value for key, value in os.environ.items() if not key.startswith("AWS_")}
            env.update({"PATH": f"{scratch}{os.pathsep}{os.environ['PATH']}",
                        "GITHUB_OUTPUT": str(output), "AWS_STUB_LOG": str(log),
                        "AWS_STUB_INSTANCE": "i-current", "AWS_STUB_HEARTBEAT": payload,
                        "AWS_ACCESS_KEY_ID": "fixture", "HONUA_CI_PRODUCER_RUNNER": "honua-build",
                        "HONUA_CI_PRODUCER_TRUSTED": "true", **(environment or {})})
            subprocess.run(["bash", str(ROOT / "scripts/ci/select-ci-producer-runner.sh")],
                           env=env, check=True, capture_output=True, timeout=5)
            return output.read_text().strip(), log.read_text().splitlines()

    def test_current_idle_gate_pool_uses_only_two_read_calls(self):
        output, calls = self.selection(heartbeat(idle=2))
        self.assertEqual(output, "runner=honua-build")
        self.assertEqual(len(calls), 2)
        self.assertTrue(calls[0].startswith("autoscaling describe-auto-scaling-groups "))
        self.assertTrue(calls[1].startswith("ssm get-parameter "))
        self.assertIn("/gate-runner/heartbeat", calls[1])

    def test_disabled_untrusted_or_uncredentialed_events_never_call_aws(self):
        for environment in ({"HONUA_CI_PRODUCER_RUNNER": ""},
                            {"HONUA_CI_PRODUCER_RUNNER": "honua-heavy"},
                            {"HONUA_CI_PRODUCER_TRUSTED": "false"}, {"AWS_ACCESS_KEY_ID": ""}):
            with self.subTest(environment=environment):
                self.assertEqual(self.selection(heartbeat(idle=2), environment),
                                 ("runner=ubuntu-latest", []))

    def test_invalid_busy_stale_and_wrong_instance_proofs_fail_closed(self):
        fresh = json.loads(heartbeat(idle=2))
        overrides = [{"idle": value} for value in (0, 1, -1, 2.5, "2", True, None)]
        overrides += [{"at": value} for value in (0, int(time.time()) + 600, "1", True, None)]
        overrides += [{"instance": "i-old"}, {"at": int(time.time()) - 100}]
        payloads = [json.dumps({**fresh, **values}) for values in overrides]
        payloads += ["{", "null", "[]", "{}", "true", '"text"',
                     json.dumps({**fresh, "idle": 0, "heavy": {"version": 1, "idle": 8}})]
        for payload in payloads:
            with self.subTest(payload=payload):
                output, calls = self.selection(payload)
                self.assertEqual(output, "runner=ubuntu-latest")
                self.assertEqual(len(calls), 2)

    def test_unavailable_instance_and_aws_failure_never_wake_or_retry(self):
        for environment in ({"AWS_STUB_INSTANCE": "None"}, {"AWS_STUB_FAIL": "1"}):
            with self.subTest(environment=environment):
                output, calls = self.selection(heartbeat(idle=2), environment)
                self.assertEqual(output, "runner=ubuntu-latest")
                self.assertEqual(len(calls), 1)
                self.assertTrue(calls[0].startswith("autoscaling describe-auto-scaling-groups "))

    def test_multiple_heartbeat_documents_fail_closed_in_either_order(self):
        fresh = heartbeat(idle=2)
        for payload in ("{}\n" + fresh, fresh + "\n{}", fresh + "\n" + fresh, ""):
            with self.subTest(payload=payload):
                output, calls = self.selection(payload)
                self.assertEqual(output, "runner=ubuntu-latest")
                self.assertEqual(len(calls), 2)

    def test_workflow_isolates_producer_and_contains_setup_failure(self):
        jobs = yaml.safe_load((ROOT / ".github/workflows/ci.yml").read_text())["jobs"]
        selector, build = jobs["producer-runner"], jobs["build"]
        self.assertEqual(build["runs-on"], "${{ needs.producer-runner.outputs.runner || 'ubuntu-latest' }}")
        self.assertEqual(build["if"], selector["if"])
        self.assertIn("producer-runner", build["needs"])
        self.assertEqual(selector["outputs"]["runner"], "${{ steps.select.outputs.runner || 'ubuntu-latest' }}")
        self.assertEqual(selector["permissions"], {"contents": "read", "id-token": "write"})
        for step in selector["steps"]:
            self.assertTrue(step["continue-on-error"])
            self.assertEqual(step["timeout-minutes"], 1)
            self.assertIn("HONUA_CI_PRODUCER_RUNNER == 'honua-build'", step["if"])
            self.assertIn("github.event.pull_request.head.repo.full_name == github.repository", step["if"])
            self.assertIn("github.event.workflow_run.head_repository.full_name == github.repository", step["if"])
        self.assertEqual(build["env"]["DOTNET_PROCESSOR_COUNT"], "4")
        self.assertEqual(build["env"]["DOTNET_CLI_USE_MSBUILD_SERVER"], "false")
        self.assertEqual(build["env"]["MSBUILDDISABLENODEREUSE"], "1")
        self.assertNotIn("DOTNET_INSTALL_DIR", build["env"])
        self.assertIn(
            'echo "DOTNET_INSTALL_DIR=$RUNNER_TEMP/honua-ci-producer-sdk" >> "$GITHUB_ENV"',
            build["steps"][0]["run"],
        )
        for step in build["steps"]:
            if "sudo " in step.get("run", ""):
                self.assertEqual(step["if"], "runner.environment == 'github-hosted'")
            if step.get("run", "").startswith(("dotnet build ", "scripts/ci/dotnet-restore-retry.sh ")):
                self.assertIn("-maxcpucount:4", step["run"])

if __name__ == "__main__":
    unittest.main()
