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
    "docker-build", "quickstart-console-smoke",
}
HOSTED_JOBS = {
    "pr-template-check", "pr-readiness", "supersede-trailing-runs", "changes", "targeted-shards",
    "ci-router-validation", "dotnet-foundation-tests", "js-typecheck", "test-all",
    "merge-queue-gate", "ci-gate",
}
REUSABLE_JOBS = {
    "store-image-proofs": "./.github/workflows/store-image-size.yml",
    "geoparquet-interop": "./.github/workflows/geoparquet-interop.yml",
    "geoarrow-interop": "./.github/workflows/geoarrow-interop-fixture.yml",
    "producer-runner": "./.github/workflows/advisory-runner-select.yml",
}
PRODUCER_RUNNER = "${{ fromJSON(needs.producer-runner.outputs.runner || '\"ubuntu-latest\"') }}"
PINNED_JOBS = {
    "worker-gdal-tests", "python-integration-tests", "js-integration-tests",
    "esri-leaflet-browser-tests", "maplibre-compat",
}


# Forks never reach self-hosted compute; PR Gate shards take the pool only when the
# selector marked the entry `heavy` (compute-affected-shards.py place_heavy_shards).
FORK_GUARD = (
    " && (!github.event.pull_request || github.event.pull_request.head.repo.full_name == github.repository)"
    " && (!github.event.workflow_run || github.event.workflow_run.head_repository.full_name == github.repository)"
    " && fromJSON(vars.HONUA_HEAVY_RUNNER)"
)
TRUSTED_HEAVY_RUNNER = "vars.HONUA_HEAVY_RUNNER" + FORK_GUARD
HEAVY_SHARD_RUNNER = "vars.HONUA_HEAVY_RUNNER && matrix.heavy" + FORK_GUARD


def planned_heavy(key):
    """A start job takes the heavy pool only when runner-select planned it."""
    return ("contains(needs.runner-select.outputs.start_heavy, format(" + key + ")) && vars.HONUA_HEAVY_RUNNER"
            + FORK_GUARD)


# GitHub's contains() and format() for the node expression probes.
JS_HELPERS = """const EXPRESSION_HELPERS = {
  contains: (haystack, needle) => String(haystack ?? '').toLowerCase().includes(String(needle).toLowerCase()),
  format: (template, ...values) => template.replace(/\\{(\\d+)\\}/g, (_, index) => String(values[index])),
};
"""


def bracket_contexts(expression):
    """Rewrites hyphenated context keys into JavaScript property access."""
    for context in ("needs.runner-select", "needs.pr-gate", "matrix.analyzer-partition",
                    "matrix.analyzer-test-partition", "matrix.test-group", "strategy.job-index"):
        head, key = context.split(".", 1)
        expression = expression.replace(context, f"{head}['{key}']")
    return expression


FAST_RUNNER = "${{ " + planned_heavy("',{0},', matrix.test-group") + " || 'ubuntu-latest' }}"
ANALYZER_TESTS_RUNNER = ("matrix.analyzer-partition == 'tests' && ("
                         + planned_heavy("',analyzers-{0},', matrix.analyzer-test-partition") + " || 'ubuntu-latest') || ")
# Native AOT is not a matrix: every trusted run takes the heavy pool, forks stay hosted.
AOT_RUNNER = "${{ vars.HONUA_HEAVY_RUNNER" + FORK_GUARD + " || vars.HONUA_CI_HEAVY_RUNNER || 'ubuntu-latest' }}"
# Full runs (the ci.yml run-name rule) take free standard hosted runners; affected runs keep the pool.
FULL_RUN_HOSTED = ("(github.event_name == 'schedule' || github.event_name == 'workflow_dispatch'"
                   " && (inputs.full_ci || !inputs.selective_base)) && 'ubuntu-latest' || ")
SHARD_JOBS = {
    "ci.yml": ("server-tests", "${{ " + FULL_RUN_HOSTED + TRUSTED_HEAVY_RUNNER
               + " || vars.HONUA_CI_HEAVY_RUNNER || 'ubuntu-latest' }}"),
    "pr-gate.yml": ("affected-shard", "${{ needs.runner-select.outputs.heavy_ready == 'true' && " + HEAVY_SHARD_RUNNER + " || 'ubuntu-latest' }}"),
}


def ephemeral_port_jobs(workflow):
    """Shard lanes that pass the assigned Postgres port inline, not via the action."""
    names = [SHARD_JOBS[workflow][0]]
    if workflow == "pr-gate.yml":
        names.append("affected-shard-held")
    return names


class ShardRunnerRoutingTests(unittest.TestCase):
    def shard_job(self, workflow):
        name, _ = SHARD_JOBS[workflow]
        return yaml.safe_load((ROOT / ".github/workflows" / workflow).read_text())["jobs"][name]

    def shard_jobs(self, workflow):
        jobs = yaml.safe_load((ROOT / ".github/workflows" / workflow).read_text())["jobs"]
        return [(name, jobs[name]) for name in ephemeral_port_jobs(workflow)]

    def test_shards_use_their_fork_guarded_pool_with_hosted_fallback(self):
        for workflow, (_, runs_on) in SHARD_JOBS.items():
            with self.subTest(workflow=workflow):
                self.assertEqual(self.shard_job(workflow)["runs-on"], runs_on)

    def test_full_runs_take_hosted_runners_and_affected_runs_the_heavy_pool(self):
        expression = self.shard_job("ci.yml")["runs-on"]
        self.assertNotIn("strategy.job-index", expression)
        probe = """const {expression, event, fork, heavy, fallback, inputs} = JSON.parse(require('fs').readFileSync(0, 'utf8'));
const repository = 'owner/repo', source = fork ? 'fork/repo' : repository;
const payload = event === 'pull_request' ? {pull_request: {head: {repo: {full_name: source}}}} :
    event === 'workflow_run' ? {workflow_run: {head_repository: {full_name: source}}} : {};
const github = {repository, event_name: event, event: payload}, vars = {HONUA_HEAVY_RUNNER: heavy, HONUA_CI_HEAVY_RUNNER: fallback};
process.stdout.write(JSON.stringify(new Function('github', 'vars', 'inputs', 'fromJSON', `return (${expression});`)(github, vars, inputs, JSON.parse)));
"""
        pool = {"group": "honua-heavy", "labels": ["honua-heavy"]}
        affected = {"full_ci": False, "selective_base": "a" * 40}
        runs = [("schedule", {}, True), ("workflow_dispatch", {"full_ci": False, "selective_base": ""}, True),
                ("workflow_dispatch", {**affected, "full_ci": True}, True), ("workflow_dispatch", affected, False),
                ("push", {}, False), ("pull_request", {}, False), ("workflow_run", {}, False)]
        for (event, inputs, full), fork, configured, fallback in product(
                runs, [False, True], [False, True], ["", "ubuntu-24.04"]):
            trusted = event not in {"pull_request", "workflow_run"} or not fork
            expected = "ubuntu-latest" if full else pool if configured and trusted else fallback or "ubuntu-latest"
            context = {"expression": expression[3:-2].strip(), "event": event, "inputs": inputs,
                       "fork": fork, "heavy": json.dumps(pool) if configured else "",
                       "fallback": fallback}
            with self.subTest(event=event, inputs=inputs, fork=fork, configured=configured, fallback=fallback):
                result = subprocess.run(["node", "-e", probe], input=json.dumps(context),
                                        text=True, capture_output=True, check=True)
                self.assertEqual(json.loads(result.stdout), expected)

    def test_postgres_uses_an_ephemeral_host_port_that_the_tests_receive(self):
        for workflow in SHARD_JOBS:
            for name, job in self.shard_jobs(workflow):
                with self.subTest(workflow=workflow, job=name):
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
        if workflow in SHARD_JOBS and name in ephemeral_port_jobs(workflow):
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

    def test_full_server_builds_use_the_fork_guarded_heavy_pool_with_hosted_fallback(self):
        for name in ("aot-build", "core-package-compatibility"):
            with self.subTest(job=name):
                self.assertEqual(self.jobs[name]["runs-on"], AOT_RUNNER)

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
        self.assertEqual(set(self.jobs), HEAVY_JOBS | HOSTED_JOBS | PINNED_JOBS | set(REUSABLE_JOBS) | {"build", "aot-build", "core-package-compatibility", SHARD_JOBS["ci.yml"][0]})


PR_GATE_SELECTED_JOBS = {"pr-gate", "server-test-producer"}
SELECTED_RUNNER = (
    "${{ (!github.event.pull_request || github.event.pull_request.head.repo.full_name == github.repository)"
    " && needs.runner-select.outputs.runner || 'ubuntu-latest' }}"
)
PR_GATE_LIGHT_JOBS = {"runner-select", "format", "affected-shards-select", "affected-shards", "ci-router-validation", "required"}
# Pull-request gate jobs stay hosted. qualify_light on a trunk dispatch is the
# only path onto honua-light. HONUA_LIGHT_RUNNER places the non-gate workflows.
LIGHT_RUNNER = (
    "${{ github.event_name == 'workflow_dispatch' && github.ref == 'refs/heads/trunk'"
    " && inputs.qualify_light && fromJSON('{\"group\":\"honua-light\",\"labels\":[\"honua-light\"]}')"
    " || 'ubuntu-latest' }}"
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
        probe = JS_HELPERS + """const {expression, light, fork, selected, draft, partition, planned} = JSON.parse(require('fs').readFileSync(0, 'utf8'));
const matrix = {'analyzer-partition': partition, 'analyzer-test-partition': 'server', 'test-group': 'architecture'};
const inputs = {qualify_light: false};
const github = {event_name: 'pull_request', ref: 'refs/pull/1/merge', repository: 'owner/repo', event: {pull_request: {draft, head: {repo: {full_name: fork ? 'fork/repo' : 'owner/repo'}}}}};
const needs = {'runner-select': {outputs: {runner: selected, start_heavy: planned}}};
const vars = {HONUA_PR_GATE_RUNNER: 'honua-build', HONUA_HEAVY_RUNNER: '{"group":"honua-heavy"}', HONUA_LIGHT_RUNNER: light};
process.stdout.write(JSON.stringify(new Function('context', `const {github, inputs, matrix, needs, vars, fromJSON, contains, format} = context; return (${expression});`)({github, inputs, matrix, needs, vars, fromJSON: JSON.parse, ...EXPRESSION_HELPERS})));
"""
        pool = {"group": "honua-light", "labels": ["honua-light"]}
        scenarios = [
            ("", False, False, "ubuntu-latest"),
            ("", False, True, "ubuntu-latest"),
            (json.dumps(pool), False, False, "ubuntu-latest"),
            (json.dumps(pool), True, False, "ubuntu-latest"),
        ]
        for name, selected, scenario, partition, planned in product(
            ["runner-select", "format", "fast-tests"], ["honua-build", "ubuntu-latest", ""], scenarios, ["all", "source", "tests"],
            [",", "", ",unit-fast,analyzers-other,"],
        ):
            light, fork, draft, expected = scenario
            if name == "fast-tests" or (name == "format" and partition == "tests"):
                expected = "ubuntu-latest"
            runner = self.jobs[name]["runs-on"]
            expression = (runner.removeprefix("${{").removesuffix("}}").strip()
                          if runner.startswith("${{") else json.dumps(runner))
            context = {
                "expression": bracket_contexts(expression),
                "selected": selected, "light": light, "fork": fork, "draft": draft, "partition": partition,
                "planned": planned,
            }
            with self.subTest(job=name, selected=selected, fork=fork, draft=draft, planned=planned):
                self.assertEqual(self.evaluate_routing(probe, context), expected)
        self.assertEqual(self.jobs["format"]["needs"], "runner-select")
        self.assertEqual(self.jobs["fast-tests"]["needs"], "runner-select")
        self.assertEqual(self.jobs["fast-server-tests"]["needs"], ["runner-select", "server-test-producer"])
        self.assertEqual(self.jobs["format"]["if"], DRAFT_FORMAT_ADMISSION)
        self.assertNotIn("if", self.jobs["fast-tests"])

    @staticmethod
    def evaluate_routing(probe, context):
        result = subprocess.run(
            ["node", "-e", probe], input=json.dumps(context),
            text=True, capture_output=True, check=True,
        )
        return json.loads(result.stdout)

    def test_pr_gate_control_jobs_stay_hosted_unless_a_trunk_dispatch_qualifies_light(self):
        for name in PR_GATE_LIGHT_JOBS:
            with self.subTest(job=name):
                expected = LIGHT_RUNNER
                if name == "format":
                    expected = expected.replace("${{ ", "${{ " + ANALYZER_TESTS_RUNNER, 1)
                self.assertEqual(self.jobs[name]["runs-on"], expected)

    def test_light_qualification_is_a_default_off_boolean(self):
        workflow = yaml.safe_load((ROOT / ".github/workflows/pr-gate.yml").read_text())
        dispatch = workflow[True]["workflow_dispatch"]
        self.assertIsInstance(dispatch, dict)
        self.assertEqual(set(dispatch["inputs"]), {"qualify_light"})
        qualification = dispatch["inputs"]["qualify_light"]
        self.assertEqual(qualification["type"], "boolean")
        self.assertIs(qualification["default"], False)

    def test_only_trunk_dispatch_can_override_the_disabled_light_pool(self):
        probe = JS_HELPERS + """const {expression, event, ref, qualify, light, fork, partition} = JSON.parse(require('fs').readFileSync(0, 'utf8'));
const matrix = {'analyzer-partition': partition, 'analyzer-test-partition': 'other'};
const needs = {'runner-select': {outputs: {start_heavy: ','}}};
const inputs = {qualify_light: qualify};
const github = {event_name: event, ref, repository: 'owner/repo', event: {}};
if (event === 'pull_request') github.event.pull_request = {head: {repo: {full_name: fork ? 'fork/repo' : 'owner/repo'}}};
if (event === 'workflow_run') github.event.workflow_run = {head_repository: {full_name: fork ? 'fork/repo' : 'owner/repo'}};
const vars = {HONUA_LIGHT_RUNNER: light, HONUA_HEAVY_RUNNER: '{"group":"honua-heavy"}'};
process.stdout.write(JSON.stringify(new Function('context', `const {github, inputs, matrix, needs, vars, fromJSON, contains, format} = context; return (${expression});`)({github, inputs, matrix, needs, vars, fromJSON: JSON.parse, ...EXPRESSION_HELPERS})));
"""
        configured_pool = {"group": "configured-light", "labels": ["configured-light"]}
        for event, ref, qualify, light, fork, partition in product(
            ["workflow_dispatch", "pull_request", "workflow_run"],
            ["refs/heads/trunk", "refs/heads/feature", "refs/tags/trunk"],
            [True, False, None], ["", json.dumps(configured_pool)], [True, False], ["all", "source", "tests"],
        ):
            expected = "ubuntu-latest"
            if event == "workflow_dispatch" and ref == "refs/heads/trunk" and qualify and partition != "tests":
                expected = {"group": "honua-light", "labels": ["honua-light"]}
            context = {"expression": bracket_contexts(self.jobs["format"]["runs-on"][3:-2].strip()),
                       "event": event, "ref": ref, "qualify": qualify, "light": light, "fork": fork, "partition": partition}
            with self.subTest(event=event, ref=ref, qualify=qualify, light=light, fork=fork):
                self.assertEqual(self.evaluate_routing(probe, context), expected)

    def test_light_jobs_pin_temp_to_the_runner_directory_before_other_work(self):
        for name in PR_GATE_LIGHT_JOBS:
            with self.subTest(job=name):
                step = self.jobs[name]["steps"][0]
                self.assertEqual(step["name"], "Use the job's own temp directory")
                for key in ("TMPDIR", "TEMP", "TMP"):
                    self.assertIn(f'echo "{key}=${{RUNNER_TEMP}}"', step["run"])
                self.assertIn("GITHUB_ENV", step["run"])

    def test_remaining_control_jobs_stay_hosted(self):
        planned = {SHARD_JOBS["pr-gate.yml"][0], "affected-shard-held", "fast-tests", "fast-server-tests"}
        for name in set(self.jobs) - PR_GATE_SELECTED_JOBS - PR_GATE_LIGHT_JOBS - planned:
            with self.subTest(job=name):
                self.assertEqual(self.jobs[name]["runs-on"], "ubuntu-latest")

    def test_planned_start_jobs_use_the_fork_guarded_heavy_pool(self):
        self.assertEqual(self.jobs["fast-tests"]["runs-on"], FAST_RUNNER)
        self.assertEqual(self.jobs["fast-server-tests"]["runs-on"], FAST_RUNNER)
        probe = JS_HELPERS + """const {expression, planned, fork, heavy, group, partition} = JSON.parse(require('fs').readFileSync(0, 'utf8'));
const matrix = {'test-group': group, 'analyzer-partition': partition, 'analyzer-test-partition': group};
const github = {event_name: 'pull_request', ref: 'refs/pull/1/merge', repository: 'owner/repo', event: {pull_request: {head: {repo: {full_name: fork ? 'fork/repo' : 'owner/repo'}}}}};
const needs = {'runner-select': {outputs: {start_heavy: planned}}};
const inputs = {qualify_light: false}, vars = {HONUA_HEAVY_RUNNER: heavy, HONUA_LIGHT_RUNNER: ''};
process.stdout.write(JSON.stringify(new Function('context', `const {github, inputs, matrix, needs, vars, fromJSON, contains, format} = context; return (${expression});`)({github, inputs, matrix, needs, vars, fromJSON: JSON.parse, ...EXPRESSION_HELPERS})));
"""
        pool = {"group": "honua-heavy", "labels": ["honua-heavy"]}
        cases = [("fast-tests", "all", "architecture", ",architecture,"), ("format", "tests", "server", ",analyzers-server,"),
                 ("format", "tests", "other", ",analyzers-other,"), ("format", "source", "other", ",analyzers-other,")]
        for (name, partition, group, key), planned, fork, configured in product(
            cases, ["key", ",", "", ",unit-fast,analyzers-x,"], [False, True], [False, True],
        ):
            planned = key if planned == "key" else planned
            context = {"expression": bracket_contexts(self.jobs[name]["runs-on"][3:-2].strip()), "planned": planned,
                       "fork": fork, "heavy": json.dumps(pool) if configured else "", "group": group, "partition": partition}
            heavy = planned == key and configured and not fork and partition != "source"
            with self.subTest(job=name, group=group, planned=planned, fork=fork, configured=configured):
                self.assertEqual(self.evaluate_routing(probe, context), pool if heavy else "ubuntu-latest")

    def test_fast_tests_isolate_temp_and_skip_hosted_only_cleanup(self):
        steps = self.jobs["fast-tests"]["steps"]
        self.assertEqual(steps[0]["name"], "Use the job's own temp directory")
        cleanup = next(step for step in steps if step.get("name") == "Free disk space")
        self.assertTrue(cleanup["if"].endswith(" && runner.environment == 'github-hosted'"))
        self.assertIn("sudo", cleanup["run"])

    def test_heavy_proof_flows_through_existing_dependencies(self):
        self.assertEqual(self.jobs["runner-select"]["outputs"]["heavy_ready"],
                         "${{ steps.select.outputs.heavy_ready || 'false' }}")
        self.assertEqual(self.jobs["runner-select"]["outputs"]["start_heavy"],
                         "${{ steps.select.outputs.start_heavy || ',' }}")
        select = next(step for step in self.jobs["runner-select"]["steps"] if step.get("id") == "select")
        self.assertEqual(select["env"]["HONUA_HEAVY_RUNNER"], "${{ vars.HONUA_HEAVY_RUNNER }}")
        self.assertEqual(self.jobs["pr-gate"]["outputs"]["heavy_ready"],
                         "${{ needs.runner-select.outputs.heavy_ready }}")
        self.assertEqual(self.jobs["affected-shard"]["needs"],
                         ["affected-shards-select", "server-test-producer", "runner-select"])
        self.assertEqual(self.jobs["affected-shard-held"]["needs"],
                         ["affected-shards-select", "pr-gate", "server-test-producer"])
        self.assertEqual(
            self.jobs["affected-shard-held"]["runs-on"],
            "${{ needs.pr-gate.outputs.heavy_ready == 'true' && " + HEAVY_SHARD_RUNNER + " || 'ubuntu-latest' }}",
        )

    def test_only_proven_same_repo_marked_shards_use_heavy_runners(self):
        probe = """const {expression, ready, fork, marked, heavy} = JSON.parse(require('fs').readFileSync(0, 'utf8'));
const github = {repository: 'owner/repo', event: {pull_request: {head: {repo: {full_name: fork ? 'fork/repo' : 'owner/repo'}}}}};
const needs = {'pr-gate': {outputs: {heavy_ready: ready}}, 'runner-select': {outputs: {heavy_ready: ready}}};
const vars = {HONUA_HEAVY_RUNNER: heavy}, matrix = marked === null ? {} : {heavy: marked};
process.stdout.write(JSON.stringify(new Function('context', `const {github, needs, vars, matrix, fromJSON} = context; return (${expression});`)({github, needs, vars, matrix, fromJSON: JSON.parse})));
"""
        pool = {"group": "honua-heavy", "labels": ["honua-heavy"]}
        for job in ("affected-shard", "affected-shard-held"):
            expression = self.jobs[job]["runs-on"].removeprefix("${{").removesuffix("}}").strip()
            expression = expression.replace("needs.runner-select", "needs['runner-select']").replace("needs.pr-gate", "needs['pr-gate']")
            for ready, fork, marked, configured in product(["true", "false", ""], [False, True], [True, False, None], [False, True]):
                context = {"expression": expression, "ready": ready, "fork": fork,
                           "marked": marked, "heavy": json.dumps(pool) if configured else ""}
                expected = pool if ready == "true" and not fork and marked is True and configured else "ubuntu-latest"
                with self.subTest(job=job, ready=ready, fork=fork, marked=marked, configured=configured):
                    self.assertEqual(self.evaluate_routing(probe, context), expected)

    def test_selection_cannot_fail_and_is_inert_without_the_variable_or_in_enforce_mode(self):
        select = self.jobs["runner-select"]
        self.assertEqual(select["if"], READY_ONLY)
        self.assertEqual(select["outputs"]["runner"], "${{ steps.select.outputs.runner || 'ubuntu-latest' }}")
        self.assertEqual(select["permissions"], {"contents": "read", "id-token": "write"})
        for step in select["steps"]:
            with self.subTest(step=step.get("name") or step.get("uses")):
                if step.get("name") == "Use the job's own temp directory":
                    self.assertNotIn("if", step)
                    self.assertEqual(step["timeout-minutes"], 1)
                    continue
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
        for workflow, job in (("pr-gate.yml", "runner-select"), ("advisory-runner-select.yml", "select")):
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
                         if key not in {"AWS_ACCESS_KEY_ID", "GITHUB_STEP_SUMMARY", "HONUA_HEAVY_RUNNER"}}
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
                self.assertEqual(fields, {"runner": runner, "heavy_ready": ready, "start_heavy": ","})
        for invalid in ["{", "null", "[]", "{}"]:
            with self.subTest(invalid=invalid):
                fields, _ = self.run_selection_details(HostScenario(heartbeat=invalid), WAKE_CREDENTIALS)
                self.assertEqual(fields, {"runner": "ubuntu-latest", "heavy_ready": "false", "start_heavy": ","})

    def test_multiple_heartbeat_documents_never_admit_either_pool(self):
        fresh = json.loads(heartbeat(idle=2))
        fresh["heavy"] = {"version": 1, "idle": 2}
        proof = json.dumps(fresh)
        for payload in ("{}\n" + proof, proof + "\n{}", proof + "\n" + proof, ""):
            with self.subTest(payload=payload):
                fields, _ = self.run_selection_details(HostScenario(heartbeat=payload), WAKE_CREDENTIALS)
                self.assertEqual(fields, {"runner": "ubuntu-latest", "heavy_ready": "false", "start_heavy": ","})

    def test_idle_heavy_runners_beyond_the_shard_reserve_take_start_jobs(self):
        pool = {**WAKE_CREDENTIALS, "HONUA_HEAVY_RUNNER": '{"group":"honua-heavy","labels":["honua-heavy"]}'}
        jobs = ["analyzers-server", "architecture", "server-fast", "analyzers-other"]
        cases = [  # (gate idle, heavy idle) -> producer runner, planned start jobs
            ((2, 8), "honua-build", jobs[:2]), ((2, 7), "honua-build", jobs[:2]), ((2, 6), "honua-build", jobs[:1]),
            ((2, 5), "honua-build", jobs[:1]), ((2, 4), "honua-build", []), ((2, 0), "honua-build", []),
            ((1, 8), "honua-heavy", []), ((0, 7), "honua-heavy", []), ((1, 6), "ubuntu-latest", jobs[:1]),
            ((1, 5), "ubuntu-latest", jobs[:1]), ((1, 4), "ubuntu-latest", []), ((0, 2), "ubuntu-latest", []),
        ]
        for (idle, heavy_idle), runner, planned in cases:
            proof = json.loads(heartbeat(idle=idle))
            proof["heavy"] = {"version": 1, "idle": heavy_idle}
            with self.subTest(idle=idle, heavy_idle=heavy_idle):
                fields, _ = self.run_selection_details(HostScenario(heartbeat=json.dumps(proof)), pool)
                self.assertEqual(fields, {"runner": runner, "heavy_ready": str(heavy_idle > 0).lower(),
                                          "start_heavy": "," + "".join(f"{job}," for job in planned)})

    def test_only_a_label_that_alone_selects_the_heavy_pool_moves_producers(self):
        proof = json.loads(heartbeat(idle=0))
        proof["heavy"] = {"version": 1, "idle": 8}
        cases = [('{"group":"honua-heavy","labels":["self-hosted","honua-heavy"]}', "honua-heavy"),
                 ('{"group":"honua-heavy","labels":["custom"]}', "custom"), ('["honua-heavy"]', "honua-heavy"),
                 ('"honua-heavy"', "honua-heavy"), ('{"group":"honua-heavy","labels":["self-hosted","linux"]}', None),
                 ('["self-hosted","honua-heavy"]', None), ('"honua-heavy"\n"other"', None)]
        for configured, label in cases:
            with self.subTest(configured=configured):
                environment = {**WAKE_CREDENTIALS, "HONUA_HEAVY_RUNNER": configured}
                fields, _ = self.run_selection_details(HostScenario(heartbeat=json.dumps(proof)), environment)
                self.assertEqual(fields["runner"], label or "ubuntu-latest")
                self.assertEqual(fields["start_heavy"], ",")

    def test_ready_gate_pool_keeps_producers_without_a_heavy_label(self):
        proof = json.loads(heartbeat(idle=2))
        proof["heavy"] = {"version": 1, "idle": 8}
        for configured in ("", '{"group":"honua-heavy"}', '["self-hosted","x64"]'):
            with self.subTest(configured=configured):
                environment = {**WAKE_CREDENTIALS, "HONUA_HEAVY_RUNNER": configured}
                fields, _ = self.run_selection_details(HostScenario(heartbeat=json.dumps(proof)), environment)
                self.assertEqual(fields, {"runner": "honua-build", "heavy_ready": "true", "start_heavy": ","})

    def test_start_jobs_stay_hosted_without_a_plain_heavy_label_or_valid_proof(self):
        fresh = json.loads(heartbeat(idle=0))
        valid = json.dumps({**fresh, "heavy": {"version": 1, "idle": 8}})
        cases = [(valid, ""), (valid, '{"group":"honua-heavy"}'), (valid, '{"group":"g","labels":["bad label"]}'),
                 (valid, "{"), (json.dumps({**fresh, "heavy": {"version": 1, "idle": 9}}), '["honua-heavy"]'),
                 (json.dumps({**fresh, "instance": "i-old", "heavy": {"version": 1, "idle": 8}}), '["honua-heavy"]')]
        for payload, configured in cases:
            with self.subTest(payload=payload, configured=configured):
                environment = {**WAKE_CREDENTIALS, "HONUA_HEAVY_RUNNER": configured}
                fields, _ = self.run_selection_details(HostScenario(heartbeat=payload), environment)
                self.assertEqual((fields["runner"], fields["start_heavy"]), ("ubuntu-latest", ","))

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


class CiProducerRoutingTests(unittest.TestCase):
    def test_producer_uses_the_shared_advisory_selector(self):
        jobs = yaml.safe_load((ROOT / ".github/workflows/ci.yml").read_text())["jobs"]
        selector, build = jobs["producer-runner"], jobs["build"]
        self.assertEqual(build["runs-on"], PRODUCER_RUNNER)
        self.assertEqual(selector["if"], build["if"])
        self.assertIn("producer-runner", build["needs"])
        self.assertEqual(selector["with"], {"hosted-runner": "ubuntu-latest"})
        self.assertEqual(selector["secrets"], {"wake-role": "${{ secrets.HONUA_PR_GATE_WAKE_ROLE_ARN }}"})
        self.assertEqual(selector["permissions"], {"contents": "read", "id-token": "write"})

    def test_producer_takes_the_selected_heavy_pool_else_hosted(self):
        heavy = {"group": "honua-heavy", "labels": ["honua-heavy"]}
        for selected, expected in ((json.dumps(heavy), heavy), ('"ubuntu-latest"', "ubuntu-latest"), ("", "ubuntu-latest")):
            with self.subTest(selected=selected):
                self.assertEqual(evaluate_producer_runner(selected), expected)

    def test_producer_bounds_workers_and_contains_setup_failure(self):
        build = yaml.safe_load((ROOT / ".github/workflows/ci.yml").read_text())["jobs"]["build"]
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


def evaluate_producer_runner(selected):
    """Evaluates the Build & Format runs-on expression for one selector output."""
    expression = PRODUCER_RUNNER.removeprefix("${{").removesuffix("}}").strip()
    expression = expression.replace("needs.producer-runner", "needs['producer-runner']")
    probe = ("const {expression, selected} = JSON.parse(require('fs').readFileSync(0, 'utf8'));"
             "const needs = {'producer-runner': {outputs: {runner: selected}}};"
             "process.stdout.write(JSON.stringify(new Function('needs', 'fromJSON',"
             " `return (${expression});`)(needs, JSON.parse)));")
    context = json.dumps({"expression": expression, "selected": selected})
    result = subprocess.run(["node", "-e", probe], input=context, text=True, capture_output=True, check=True)
    return json.loads(result.stdout)


if __name__ == "__main__":
    unittest.main()
