#!/usr/bin/env python3
"""Assert shared GDAL provisioning stays bounded and the real proof fails closed."""

from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path
from typing import NamedTuple

import yaml


ROOT = Path(__file__).resolve().parents[3]
JOB = "pr-gate-tests"
PROOF = "Prove the real-GDAL CLI cases run on the required gate"
PROVISION = "Provision the GDAL CLI packages for the real-GDAL proof"
WORKER_JOB = "worker-gdal-tests"
WORKER_STEP = "Install GDAL CLI"
FIDELITY = "Execute import and independent vector export fidelity proofs"
FIDELITY_UPLOAD = "Upload import and independent vector export fidelity evidence"
PROOF_TIMEOUT_MINUTES = 10
ACTION = "./.github/actions/provision-gdal-cli"
# The slowest successful `Tests and runtime proofs` job across 2026-10-24..26
# (65 runs) took 2454 s, about 41 minutes, with a ~30 s provisioning step.
# Round up so the job timeout must hold that plus the full provisioning budget.
JOB_BASELINE_SECONDS = 45 * 60

DURATION = r"(\d+)s?"


class ContractError(Exception):
    """A structural violation of the apt provisioning contract."""


def _step(steps: list, name: str) -> tuple[int, dict]:
    matches = [
        (index, step)
        for index, step in enumerate(steps)
        if isinstance(step, dict) and step.get("name") == name
    ]
    if len(matches) != 1:
        raise ContractError(f"expected exactly one step named {name!r}; found {len(matches)}")
    return matches[0]


def provisioning_worst_case_seconds(script: str) -> int:
    loop = re.search(r"for attempt in ([\d ]+); do(.*?)\n\s*done", script, re.S)
    if not loop or loop.group(1).strip() != "1 2 3":
        raise ContractError("each mirror must get three bounded attempts")
    bounded = re.search(rf"timeout --kill-after={DURATION} {DURATION} ", loop.group(2))
    if not bounded or "--download-only" not in loop.group(2) or "--error-on=any" not in loop.group(2):
        raise ContractError("each attempt must bound the update and download, failing partial fetches")
    backoff = re.search(r"sleep \$\(\(attempt \* (\d+)\)\)", loop.group(2))
    if not backoff or 'if [[ "${attempt}" -lt 3 ]]' not in loop.group(2):
        raise ContractError("each mirror must back off between attempts")
    if script.count("if ! download_packages; then") != 2:
        raise ContractError("both mirrors must use the same retry loop")
    install = re.search(
        rf'timeout --kill-after={DURATION} {DURATION} apt-get [^\n]*--no-download', script
    )
    if not install:
        raise ContractError("the final apt install must be bounded and offline")
    return 2 * (3 * (int(bounded.group(1)) + int(bounded.group(2))) + 3 * int(backoff.group(1))) + (
        int(install.group(1)) + int(install.group(2))
    )


def _require_shared_action(action_source: str) -> None:
    document = yaml.safe_load(action_source)
    steps = ((document or {}).get("runs") or {}).get("steps")
    if not isinstance(steps, list) or len(steps) != 1:
        raise ContractError("the action must delegate to the shared mirror provisioner")
    if steps[0].get("run") != "bash scripts/ci/provision-gdal-cli.sh":
        raise ContractError("the action must run the shared provisioner")
    if "continue-on-error" in steps[0]:
        raise ContractError("GDAL provisioning must stay blocking")


def _require_tripwire(proof: dict) -> None:
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
    enabled = (proof.get("env") or {}).get("HONUA_REQUIRE_GDAL_CLI")
    if enabled is not True and str(enabled) != "true":
        raise ContractError(f"{PROOF!r} must set HONUA_REQUIRE_GDAL_CLI so a missing tool fails the proof")


def _require_shared_use(document: dict, job_name: str, step_name: str) -> dict:
    job = ((document or {}).get("jobs") or {}).get(job_name)
    if not isinstance(job, dict) or not isinstance(job.get("steps"), list):
        raise ContractError(f"{job_name!r} no longer declares steps")
    _, step = _step(job["steps"], step_name)
    if step.get("uses") != ACTION:
        raise ContractError(f"{job_name} {step_name!r} must use the shared GDAL CLI provisioner")
    if "apt-get" in str(step.get("run") or ""):
        raise ContractError(f"{job_name} {step_name!r} must not install packages inline")
    return step


def validate(sources: Sources) -> None:
    workflow, script, action, worker_ci = sources
    for option in ("Acquire::Retries", "Acquire::http::Timeout", "Acquire::https::Timeout"):
        if option not in script:
            raise ContractError(f"the provisioner must configure apt's {option}")
    worst_case = provisioning_worst_case_seconds(script)
    _require_shared_action(action)
    document = yaml.safe_load(workflow)
    job = ((document or {}).get("jobs") or {}).get(JOB)
    if not isinstance(job, dict) or not isinstance(job.get("steps"), list):
        raise ContractError(f"the required workflow no longer declares the {JOB!r} job's steps")
    steps = job["steps"]
    proof_index, proof = _step(steps, PROOF)
    _require_tripwire(proof)
    provision_index, provision = _step(steps, PROVISION)
    if provision_index >= proof_index:
        raise ContractError(f"{PROVISION!r} must run before {PROOF!r}")
    if provision.get("if") != proof.get("if"):
        raise ContractError(f"{PROVISION!r} must run under exactly the same condition as {PROOF!r}")
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
            f"the {JOB!r} job timeout {job_timeout!r} minutes cannot hold its {JOB_BASELINE_SECONDS}s "
            f"observed runtime plus {step_timeout} minutes of provisioning"
        )
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

    worker = yaml.safe_load(worker_ci)
    _require_shared_use(worker, WORKER_JOB, WORKER_STEP)
    worker_job = worker["jobs"][WORKER_JOB]
    worker_enabled = (worker_job.get("env") or {}).get("HONUA_REQUIRE_GDAL_CLI")
    if worker_enabled is not True and str(worker_enabled) != "true":
        raise ContractError(f"{WORKER_JOB} must set HONUA_REQUIRE_GDAL_CLI so a missing tool fails the run")
    for step in worker_job["steps"]:
        if not isinstance(step, dict):
            continue
        if "python3-scipy" in str(step.get("run") or ""):
            raise ContractError(f"{WORKER_JOB} must not keep a second copy of the GDAL CLI install")


class Sources(NamedTuple):
    workflow: str
    script: str
    action: str
    worker: str


Change = tuple[str, str, str, int]
WORKFLOW_SOURCE = 0
SCRIPT_SOURCE = 1
ACTION_SOURCE = 2
WORKER_SOURCE = 3


def _reject(sources: Sources, label: str) -> None:
    try:
        validate(sources)
    except ContractError:
        return
    raise SystemExit(f"apt provisioning failure-injection {label} was accepted")


def _changed(original: str, mutated: str, label: str) -> str:
    if mutated == original:
        raise SystemExit(f"apt provisioning failure-injection {label} no longer applies")
    return mutated


def _patch(sources: Sources, index: int, change: Change) -> tuple[Sources, str]:
    old, new, label, count = change
    parts = list(sources)
    parts[index] = _changed(parts[index], parts[index].replace(old, new, count), label)
    return Sources(parts[0], parts[1], parts[2], parts[3]), label


def _workflow_breaks() -> tuple[Change, ...]:
    proof_anchor = '          export PATH="/usr/bin:${PATH}"\n'
    job_header = (
        "    name: PR Gate / Tests and runtime proofs\n"
        "    needs: pr-gate\n"
        "    runs-on: ubuntu-latest\n"
        "    timeout-minutes: "
    )
    return (
        (proof_anchor, "          sudo apt-get update -qq\n" + proof_anchor, "proof-installs", 1),
        (
            f"      - name: {PROOF}\n",
            f"      - name: {PROOF}\n        continue-on-error: true\n",
            "proof-continues",
            1,
        ),
        ('HONUA_REQUIRE_GDAL_CLI: "true"', 'HONUA_REQUIRE_GDAL_CLI: "false"', "proof-allows-skip", 1),
        (job_header + "85\n", job_header + "60\n", "job-timeout", 1),
        (
            "if-no-files-found: ${{ steps.fidelity-proof.outcome != 'skipped' && 'error' || 'warn' }}",
            "if-no-files-found: error",
            "upload-masks-failure",
            1,
        ),
        (
            "        uses: ./.github/actions/provision-gdal-cli\n",
            "        run: sudo apt-get install -yqq gdal-bin\n",
            "provision-inline",
            1,
        ),
    )


def _script_breaks() -> tuple[Change, ...]:
    return (
        ("sudo timeout --kill-after=15s 240s", "sudo", "unbounded-download", 1),
        ("--error-on=any", "", "update-ignores-fetch-errors", 1),
        ("for attempt in 1 2 3; do", "for attempt in 1 2 3 4; do", "too-many-attempts", 1),
        ('\'Acquire::http::Timeout "30";\'', '\'Acquire::Queue-Mode "host";\'', "no-http-timeout", 1),
        ("--no-download", "", "install-reaches-network", 1),
    )


def _worker_breaks() -> tuple[Change, ...]:
    return (
        (
            "        uses: ./.github/actions/provision-gdal-cli\n",
            "        run: sudo apt-get install -yqq gdal-bin python3-gdal python3-numpy python3-scipy\n",
            "worker-copies-install",
            1,
        ),
    )


def _injections(sources: Sources) -> tuple[tuple[Sources, str], ...]:
    groups = (
        (WORKFLOW_SOURCE, _workflow_breaks()),
        (SCRIPT_SOURCE, _script_breaks()),
        (WORKER_SOURCE, _worker_breaks()),
    )
    return tuple(_patch(sources, index, change) for index, changes in groups for change in changes)


def mirror_self_test() -> None:
    completed = subprocess.run(
        [sys.executable, str(ROOT / "scripts/ci/fixtures/test_provision_gdal_cli.py")],
        check=False,
        text=True,
        capture_output=True,
    )
    if completed.returncode != 0:
        sys.stderr.write(completed.stdout)
        sys.stderr.write(completed.stderr)
        raise SystemExit(f"provision-gdal-cli self-test failed ({completed.returncode})")


def self_test() -> None:
    sources = Sources(
        read_text(ROOT / ".github/workflows/pr-gate.yml"),
        read_text(ROOT / "scripts/ci/provision-gdal-cli.sh"),
        read_text(ROOT / ".github/actions/provision-gdal-cli/action.yml"),
        read_text(ROOT / ".github/workflows/ci.yml"),
    )
    validate(sources)
    mirror_self_test()
    for mutated, label in _injections(sources):
        _reject(mutated, label)
    print("pr-gate-apt-provisioning-self-test=ok")


def read_text(path: Path) -> str:
    return path.read_text(encoding="utf-8").replace("\r\n", "\n")


def main() -> int:
    workflow = ROOT / ".github/workflows/pr-gate.yml"
    if len(sys.argv) > 1 and sys.argv[1] != "--self-test":
        workflow = Path(sys.argv[1])
    self_test_requested = "--self-test" in sys.argv
    try:
        validate(
            Sources(
                read_text(workflow),
                read_text(ROOT / "scripts/ci/provision-gdal-cli.sh"),
                read_text(ROOT / ".github/actions/provision-gdal-cli/action.yml"),
                read_text(ROOT / ".github/workflows/ci.yml"),
            )
        )
    except ContractError as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1
    if self_test_requested:
        self_test()
    print("pr-gate-apt-provisioning=ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
