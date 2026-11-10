#!/usr/bin/env python3
"""Validate the shared native cache and required real-GDAL proof contract."""
from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path
from typing import NamedTuple

import yaml

ROOT = Path(__file__).resolve().parents[3]
JOB = "runtime-proofs"
PROOF = "Run the real-GDAL cases and prove they were not skipped"
PROVISION = "Install GDAL CLI"
WORKER_JOB = "worker-gdal-tests"
WORKER_STEP = "Install GDAL CLI"
PYTHON_JOB = "python-integration-tests"
PYTHON_STEP = "Install GDAL tools and native Parquet dependency"
FIDELITY = "Execute import and independent vector export fidelity proofs"
FIDELITY_UPLOAD = "Upload import and independent vector export fidelity evidence"
PROOF_TIMEOUT_MINUTES = 10
JOB_BASELINE_SECONDS = 45 * 60
NATIVE_BUDGET_SECONDS = 15 * 60
PYTHON_JOB_MINUTES = 25
ACTION = "./.github/actions/provision-gdal-cli"
CACHE_REVISION = "55cc8345863c7cc4c66a329aec7e433d2d1c52a9"


class ContractError(Exception):
    pass


class Sources(NamedTuple):
    workflow: str
    script: str
    action: str
    ci: str


def _step(steps: list, name: str) -> tuple[int, dict]:
    matches = [(index, step) for index, step in enumerate(steps) if step.get("name") == name]
    if len(matches) != 1:
        raise ContractError(f"expected one step named {name!r}; found {len(matches)}")
    return matches[0]


def provisioning_worst_case_seconds(script: str) -> int:
    constants = {name: int(seconds) for name, seconds in re.findall(r"^readonly (APT_\w+)=(\d+)$", script, re.M)}
    attempts = re.findall(r"for attempt in ([\d ]+); do", script)
    if attempts != ["1 2 3"] or script.count("if download_from_mirror;") != 1 or script.count("if ! download_from_mirror;") != 1:
        raise ContractError("both mirrors must use the same three-attempt download loop")
    try:
        per_mirror = 3 * (constants["APT_ATTEMPT_SECONDS"] + constants["APT_KILL_GRACE_SECONDS"]) + 3 * constants["APT_BACKOFF_SECONDS"]
        total = 2 * per_mirror + constants["APT_INSTALL_SECONDS"] + constants["APT_KILL_GRACE_SECONDS"]
    except KeyError as error:
        raise ContractError(f"missing native provisioning bound: {error}") from error
    if total > NATIVE_BUDGET_SECONDS:
        raise ContractError(f"native provisioning worst case {total}s exceeds its {NATIVE_BUDGET_SECONDS}s budget")
    required = (
        'timeout --kill-after="${APT_KILL_GRACE_SECONDS}s" "${APT_ATTEMPT_SECONDS}s"',
        'timeout --kill-after="${APT_KILL_GRACE_SECONDS}s" "${APT_INSTALL_SECONDS}s"',
        'apt-get "$@" update --error-on=any -q &&',
        'apt-get "$@" install -yq --reinstall --download-only',
        'install -yq --no-download "${install_dir}"/*.deb',
        'sleep $((attempt * APT_BACKOFF_SECONDS))',
        'if [[ "$attempt" -lt 3 ]]',
        'Acquire::Retries', 'Acquire::http::Timeout', 'Acquire::https::Timeout',
    )
    if any(needle not in script for needle in required):
        raise ContractError("native provisioning must bound each complete download attempt and install offline")
    return total


def _require_cache_action(action_source: str) -> None:
    action = yaml.safe_load(action_source)
    steps = action["runs"]["steps"]
    if len(steps) != 4:
        raise ContractError("the native action must identify, restore, verify/install and save in order")
    identify, restore, install, save = steps
    if identify.get("run") != "bash scripts/ci/provision-gdal-cli.sh resolve":
        raise ContractError("runner identity must precede the cache lookup")
    if restore.get("uses") != f"actions/cache/restore@{CACHE_REVISION}" or save.get("uses") != f"actions/cache/save@{CACHE_REVISION}":
        raise ContractError("native packages require separate pinned restore/save actions")
    if restore.get("id") != "gdal-debs" or restore.get("with") != save.get("with"):
        raise ContractError("restore and save must use the same exact cache identity")
    cache = restore["with"]
    for identity in ("runner.os", "runner.arch", "steps.resolve.outputs.image", "hashFiles('scripts/ci/provision-gdal-cli.sh')"):
        if identity not in cache.get("key", ""):
            raise ContractError(f"native cache key must include {identity}")
    if "restore-keys" in cache or cache.get("path") != "${{ steps.resolve.outputs.cache-dir }}":
        raise ContractError("native caches cannot fall back across identities")
    if install.get("run") != "bash scripts/ci/provision-gdal-cli.sh install":
        raise ContractError("cache restore must precede native verification/install")
    expected_env = {"GDAL_DEB_CACHE_HIT": "${{ steps.gdal-debs.outputs.cache-hit }}", "GDAL_PYTHON_ENVIRONMENT": "${{ inputs.python-environment }}"}
    if install.get("env") != expected_env or save.get("if") != "steps.gdal-debs.outputs.cache-hit != 'true'":
        raise ContractError("only a verified cache miss may save packages")
    if action["inputs"]["python-environment"].get("default") != "system":
        raise ContractError("the shared action must publish distro Python by default")
    if any("continue-on-error" in step or "if" in step for step in steps[:-1]):
        raise ContractError("native cache identification, restore and installation must stay blocking")


def _require_tripwire(proof: dict, job: dict) -> None:
    proof_run = str(proof.get("run") or "")
    if "apt-get" in proof_run:
        raise ContractError(
            f"{PROOF!r} must not provision packages inside its own timeout; a stalled apt mirror "
            "then consumes the whole proof budget with no output"
        )
    if proof.get("timeout-minutes") != PROOF_TIMEOUT_MINUTES:
        raise ContractError(f"{PROOF!r} must keep its {PROOF_TIMEOUT_MINUTES}-minute timeout")
    if "continue-on-error" in proof:
        raise ContractError(f"{PROOF!r} must stay blocking")
    for needle in ("dotnet test", "Category=GDAL", "count_outcome Passed", "count_outcome NotExecuted"):
        if needle not in proof_run:
            raise ContractError(f"{PROOF!r} no longer proves the Category=GDAL cases ran ({needle})")
    enabled = (proof.get("env") or job.get("env") or {}).get("HONUA_REQUIRE_GDAL_CLI")
    if enabled is not True and str(enabled) != "true":
        raise ContractError(f"{PROOF!r} must set HONUA_REQUIRE_GDAL_CLI so a missing tool fails the proof")


def _require_shared_use(document: dict, job_name: str, step_name: str) -> dict:
    job = ((document or {}).get("jobs") or {}).get(job_name)
    if not isinstance(job, dict) or not isinstance(job.get("steps"), list):
        raise ContractError(f"{job_name!r} no longer declares steps")
    _, step = _step(job["steps"], step_name)
    if "continue-on-error" in step:
        raise ContractError(f"{job_name} native provisioning must stay blocking")
    if step.get("uses") != ACTION:
        raise ContractError(f"{job_name} {step_name!r} must use the shared GDAL CLI provisioner")
    if "apt-get" in str(step.get("run") or ""):
        raise ContractError(f"{job_name} {step_name!r} must not install packages inline")
    return step


def _require_gate(sources: Sources, worst_case: int) -> None:
    document = yaml.safe_load(sources.workflow)
    job = ((document or {}).get("jobs") or {}).get(WORKER_JOB)
    if not isinstance(job, dict) or not isinstance(job.get("steps"), list):
        raise ContractError(f"the required workflow no longer declares the {WORKER_JOB!r} job's steps")
    steps = job["steps"]
    proof_index, proof = _step(steps, PROOF)
    _require_tripwire(proof, job)
    provision_index, provision = _step(steps, PROVISION)
    if provision_index >= proof_index:
        raise ContractError(f"{PROVISION!r} must run before {PROOF!r}")
    if "continue-on-error" in provision:
        raise ContractError(f"{PROVISION!r} must stay blocking")
    if provision.get("uses") != ACTION or provision.get("run"):
        raise ContractError(f"{PROVISION!r} must delegate to the shared GDAL CLI provisioner")
    step_timeout = provision.get("timeout-minutes")
    if not isinstance(step_timeout, int) or step_timeout * 60 < worst_case:
        raise ContractError(
            f"{PROVISION!r} timeout-minutes {step_timeout!r} cannot hold its {worst_case}s worst case"
        )
    job_timeout = job.get("timeout-minutes")
    if not isinstance(job_timeout, int) or job_timeout * 60 < JOB_BASELINE_SECONDS + step_timeout * 60:
        raise ContractError(
            f"the {WORKER_JOB!r} job timeout {job_timeout!r} minutes cannot hold its {JOB_BASELINE_SECONDS}s "
            f"observed runtime plus {step_timeout} minutes of provisioning"
        )
    steps = document["jobs"][JOB]["steps"]
    fidelity_index, fidelity = _step(steps, FIDELITY)
    upload_index, upload = _step(steps, FIDELITY_UPLOAD)
    fidelity_id = fidelity.get("id")
    if not fidelity_id or upload_index <= fidelity_index:
        raise ContractError(f"{FIDELITY!r} needs an id and must run before {FIDELITY_UPLOAD!r}")
    missing = str((upload.get("with") or {}).get("if-no-files-found") or "")
    expected = f"${{{{ steps.{fidelity_id}.outcome != 'skipped' && 'error' || 'warn' }}}}"
    if missing != expected:
        raise ContractError(
            f"{FIDELITY_UPLOAD!r} must error on missing evidence only when the proof ran "
            f"(if-no-files-found: {expected}); found {missing!r}"
        )



def _require_ci_native_callers(ci: dict) -> None:
    _require_shared_use(ci, WORKER_JOB, WORKER_STEP)
    worker_job = ci["jobs"][WORKER_JOB]
    worker_enabled = (worker_job.get("env") or {}).get("HONUA_REQUIRE_GDAL_CLI")
    if worker_enabled is not True and str(worker_enabled) != "true":
        raise ContractError(f"{WORKER_JOB} must set HONUA_REQUIRE_GDAL_CLI so a missing tool fails the run")
    for step in worker_job["steps"]:
        if not isinstance(step, dict):
            continue
        if "python3-scipy" in str(step.get("run") or ""):
            raise ContractError(f"{WORKER_JOB} must not keep a second copy of the GDAL CLI install")

    python = _require_shared_use(ci, PYTHON_JOB, PYTHON_STEP)
    python_job = ci["jobs"][PYTHON_JOB]
    if python_job.get("timeout-minutes") != PYTHON_JOB_MINUTES:
        raise ContractError("Python integration must retain its 25-minute job budget")
    if python.get("with") != {"python-environment": "setup-python"}:
        raise ContractError("Python integration must preserve its setup-python interpreter")
    if any("with" in step for step in worker_job["steps"] if step.get("name") == WORKER_STEP):
        raise ContractError("Worker must retain distro Python publication")
    for job in (worker_job, python_job):
        if job.get("runs-on") != "ubuntu-24.04":
            raise ContractError("native-cache callers must use the approved Ubuntu 24.04 runner")
    steps = python_job["steps"]
    indexes = [_step(steps, name)[0] for name in ("Setup Python", PYTHON_STEP, "Install Python test dependencies", "Prove GeoParquet independent-consumer execution", "Run Python Integration Tests (OGC + FeatureServer + GDAL)")]
    if indexes != sorted(indexes):
        raise ContractError("native restore/install/save must finish before Python dependencies and tests")


def validate(sources: Sources) -> None:
    worst_case = provisioning_worst_case_seconds(sources.script)
    _require_cache_action(sources.action)
    _require_gate(sources, worst_case)
    _require_ci_native_callers(yaml.safe_load(sources.ci))


def read_sources(workflow: Path) -> Sources:
    return Sources(*(path.read_text() for path in (
        workflow, ROOT / "scripts/ci/provision-gdal-cli.sh",
        ROOT / ".github/actions/provision-gdal-cli/action.yml", ROOT / ".github/workflows/ci.yml")))


def self_test(sources: Sources) -> None:
    subprocess.run(["bash", str(ROOT / "scripts/ci/provision-gdal-cli.sh"), "self-test"], check=True)
    mutations = (
        (0, "        timeout-minutes: 30\n", "        timeout-minutes: 1\n"),
        (0, f"      - name: {PROOF}\n", f"      - name: {PROOF}\n        continue-on-error: true\n"),
        (0, f"      - name: {PROVISION}\n", f"      - name: {PROVISION}\n        continue-on-error: true\n"),
        (0, "        uses: ./.github/actions/provision-gdal-cli\n", "        run: sudo apt-get install -yqq gdal-bin\n"),
        (3, "        uses: ./.github/actions/provision-gdal-cli\n", "        run: sudo apt-get install -yqq gdal-bin\n"),
        (3, f"      - name: {PYTHON_STEP}\n", f"      - name: {PYTHON_STEP}\n        continue-on-error: true\n"),
        (0, 'HONUA_REQUIRE_GDAL_CLI: "true"', 'HONUA_REQUIRE_GDAL_CLI: "false"'),
        (0, 'timeout-minutes: 75', 'timeout-minutes: 60'),
        (0, "if-no-files-found: ${{ steps.fidelity-proof.outcome != 'skipped' && 'error' || 'warn' }}", 'if-no-files-found: error'),
        (1, 'APT_ATTEMPT_SECONDS=90', 'APT_ATTEMPT_SECONDS=240'),
        (1, '"${APT_ATTEMPT_SECONDS}s"', '"1s"'),
        (1, '"${APT_INSTALL_SECONDS}s"', '"1s"'),
        (1, '--error-on=any', ''), (1, '--no-download', ''),
        (2, "${{ steps.resolve.outputs.image }}", "image"),
        (2, "${{ hashFiles('scripts/ci/provision-gdal-cli.sh') }}", "hash"),
        (2, 'actions/cache/save@', 'actions/cache@'),
        (3, 'python-environment: setup-python', 'python-environment: system'),
    )
    for index, old, new in mutations:
        changed = list(sources)
        if old not in changed[index]:
            raise ContractError(f"failure injection no longer applies: {old}")
        changed[index] = changed[index].replace(old, new)
        try:
            validate(Sources(*changed))
        except ContractError:
            continue
        raise ContractError(f"failure injection was accepted: {old}")
    for index, selector in ((2, None), (3, PYTHON_JOB)):
        changed = list(sources)
        document = yaml.safe_load(changed[index])
        steps = document["runs"]["steps"] if selector is None else document["jobs"][selector]["steps"]
        if selector is None:
            steps[2], steps[3] = steps[3], steps[2]
        else:
            native, _ = _step(steps, PYTHON_STEP)
            dependencies, _ = _step(steps, "Install Python test dependencies")
            steps[native], steps[dependencies] = steps[dependencies], steps[native]
        changed[index] = yaml.safe_dump(document, sort_keys=False)
        try:
            validate(Sources(*changed))
        except ContractError:
            continue
        raise ContractError("out-of-order native cache phases were accepted")
    print("pr-gate-apt-provisioning-self-test=ok")


def main() -> int:
    workflow = ROOT / ".github/workflows/ci.yml"
    if len(sys.argv) > 1 and sys.argv[1] != "--self-test":
        workflow = Path(sys.argv[1])
    try:
        sources = read_sources(workflow)
        validate(sources)
        if "--self-test" in sys.argv:
            self_test(sources)
    except (ContractError, subprocess.CalledProcessError) as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1
    print("pr-gate-apt-provisioning=ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
