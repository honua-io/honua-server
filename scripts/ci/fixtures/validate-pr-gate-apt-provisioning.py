#!/usr/bin/env python3
"""Assert the real-GDAL proof provisions packages outside its own timeout.

The proof step installs nothing. A shared provisioner, used by the proof and
by ci.yml's worker GDAL job, restores a .deb cache keyed on the runner image
and the resolved package versions. A hit installs that set with --no-download.
A miss retries the apt mirror, bounded by timeout, then installs offline and
saves the .deb set. An unreadable index installs the newest saved .deb files
with dpkg and does not ask apt to resolve package names. The proof still
fails closed when GDAL is missing.
"""

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
CACHE_ACTION = "actions/cache@55cc8345863c7cc4c66a329aec7e433d2d1c52a9"
RESTORE_ACTION = "actions/cache/restore@55cc8345863c7cc4c66a329aec7e433d2d1c52a9"
# The slowest successful `Tests and runtime proofs` job across 2026-10-24..26
# (65 runs) took 2454 s, about 41 minutes, with a ~30 s provisioning step.
# Round up so the job timeout must hold that plus the full provisioning budget.
JOB_BASELINE_SECONDS = 45 * 60
SHARED_RETRY_LOOPS = 1
RETRY_CALLS = 2

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


def _function_body(script: str, name: str) -> str:
    match = re.search(rf"^{name}\(\) \{{(.*?)^\}}", script, re.M | re.S)
    if not match:
        raise ContractError(f"the provisioner is missing {name}()")
    return match.group(1)


def _case_arm(script: str, function: str, label: str) -> str:
    body = _function_body(script, function)
    match = re.search(rf"[ ]*{re.escape(label)}\)\n(?P<body>.*?)\n[ ]*;;", body, re.S)
    if not match:
        raise ContractError(f"{function}() is missing the {label} arm")
    return match.group("body")


def _attempt_loops(script: str) -> list[tuple[list[int], str]]:
    return [
        ([int(value) for value in match.group(1).split()], match.group(2))
        for match in re.finditer(r"for attempt in ([\d ]+); do(.*?)\n\s*done", script, re.S)
    ]


def _retry_seconds(script: str, kind: str) -> int:
    if kind == "update":
        pattern = r"run_bounded_attempts (\d+)s \"[^\"]+\" \\\n[ ]+apt-get update --error-on=any -q\n"
    else:
        pattern = (
            r"run_bounded_attempts (\d+)s \"[^\"]+\" \\\n"
            r"[ ]+sh -c \"apt-get update --error-on=any -q && "
            r"apt-get install -yq --download-only "
        )
    match = re.search(pattern, script)
    if not match:
        raise ContractError(f"the shared retry must bound the {kind} and fail a partial apt fetch")
    return int(match.group(1))


def provisioning_worst_case_seconds(script: str) -> int:
    """Worst case of the shared retry, used for both the index and the download."""
    loops = _attempt_loops(script)
    if len(loops) != SHARED_RETRY_LOOPS:
        raise ContractError("the provisioner must share one bounded retry loop")
    attempts, body = loops[0]
    bounded = re.search(rf"timeout --kill-after={DURATION} ", body)
    if not bounded:
        raise ContractError("each provisioner attempt must run under `timeout`")
    backoff = re.search(r"sleep \$\(\(attempt \* (\d+)\)\)", body)
    if not backoff:
        raise ContractError("the provisioner must back off between attempts")
    if len(re.findall(r"run_bounded_attempts (\d+)s ", script)) != RETRY_CALLS:
        raise ContractError("index refresh and package download must both use the shared retry")
    sleeps = sum(attempt * int(backoff.group(1)) for attempt in attempts[:-1])
    kill_after = int(bounded.group(1))
    total = sum(
        len(attempts) * (kill_after + _retry_seconds(script, kind)) + sleeps
        for kind in ("update", "download")
    )
    install = re.search(
        rf"timeout --kill-after={DURATION} {DURATION} apt-get install [^\n]*--no-download",
        script,
    )
    if not install:
        raise ContractError(
            "the provisioner must finish with a bounded, offline (--no-download) apt-get install"
        )
    saved = re.search(rf"timeout --kill-after={DURATION} {DURATION} dpkg -i", script)
    if not saved:
        raise ContractError("a saved .deb set must be installed with a bounded dpkg -i")
    if (int(saved.group(1)), int(saved.group(2))) != (int(install.group(1)), int(install.group(2))):
        raise ContractError("dpkg -i must use the same bound as the offline apt install")
    resolve = re.search(
        rf"timeout --kill-after={DURATION} {DURATION} apt-get install [^\n]*--print-uris",
        script,
    )
    if not resolve:
        raise ContractError("package resolution must run under timeout")
    return (
        total
        + int(install.group(1))
        + int(install.group(2))
        + int(resolve.group(1))
        + int(resolve.group(2))
    )


def _require_offline_hit(script: str) -> None:
    mode = _function_body(script, "select_cache_mode")
    if '== "unavailable"' not in mode or '== "true"' not in mode:
        raise ContractError("install must distinguish an unreadable index from an exact cache hit")
    hit = _case_arm(script, "install_from_mode", "hit")
    miss = _case_arm(script, "install_from_mode", "miss")
    if "install_offline" not in hit or "copy_cached_debs" not in hit:
        raise ContractError("a cache hit must install the saved .deb set offline")
    for forbidden in ("download_packages", "apt-get update", "--download-only"):
        if forbidden in hit:
            raise ContractError(f"a cache hit must not reach the apt mirror via {forbidden}")
    if "download_packages" not in miss or "install_offline" not in miss or "stage_resolved_debs" not in miss:
        raise ContractError("a cache miss must download from the mirror, install offline, and stage the manifest")
    offline = _function_body(script, "install_offline")
    if "--no-download" not in offline:
        raise ContractError("the offline install must pass --no-download")
    for forbidden in ("apt-get update", "--download-only"):
        if forbidden in offline:
            raise ContractError(f"the offline install must not {forbidden}")
    verify = _function_body(script, "verify_gdal_cli")
    if "${PATH}" in verify:
        raise ContractError("GDAL tool lookup must ignore executables inherited from PATH")
    _require_saved_image(script)


def _require_cache_action(action_source: str) -> None:
    document = yaml.safe_load(action_source)
    steps = ((document or {}).get("runs") or {}).get("steps")
    if not isinstance(steps, list):
        raise ContractError("the GDAL CLI provisioner has no steps")
    resolve_steps = [step for step in steps if isinstance(step, dict) and step.get("id") == "resolve"]
    cache_steps = [step for step in steps if isinstance(step, dict) and step.get("id") == "gdal-debs"]
    if len(resolve_steps) != 1 or len(cache_steps) != 1:
        raise ContractError("the provisioner must resolve package versions and then restore the cache")
    if steps.index(resolve_steps[0]) > steps.index(cache_steps[0]):
        raise ContractError("package versions must be resolved before the cache lookup")
    resolve_run = str(resolve_steps[0].get("run") or "")
    if "provision-gdal-cli.sh resolve" not in resolve_run:
        raise ContractError("the cache key must come from the shared provisioner")
    if cache_steps[0].get("uses") != CACHE_ACTION:
        raise ContractError("the .deb set must be cached with the pinned actions/cache")
    cache_with = cache_steps[0].get("with") or {}
    key = str(cache_with.get("key") or "")
    for piece in (
        "runner.os",
        "runner.arch",
        "steps.resolve.outputs.image",
        "steps.resolve.outputs.versions",
    ):
        if piece not in key:
            raise ContractError(f"the .deb cache key must include the {piece}")
    if cache_with.get("path") != "${{ steps.resolve.outputs.cache-dir }}":
        raise ContractError("the cache must store the directory the provisioner fills on a miss")
    install_steps = [
        step
        for step in steps
        if isinstance(step, dict) and "provision-gdal-cli.sh install" in str(step.get("run") or "")
    ]
    if len(install_steps) != 1:
        raise ContractError("the provisioner must install through the shared script")
    if steps.index(cache_steps[0]) > steps.index(install_steps[0]):
        raise ContractError("the cache lookup must happen before install")
    hit = str((install_steps[0].get("env") or {}).get("GDAL_DEB_CACHE_HIT") or "")
    if "steps.gdal-debs.outputs.cache-hit" not in hit:
        raise ContractError("install must follow the actions/cache hit result")
    _require_index_fallback(steps, cache_steps[0], install_steps[0])


def _require_saved_image(script: str) -> None:
    body = _case_arm(script, "install_from_mode", "saved-image")
    if "install_saved_debs" not in body:
        raise ContractError("an unreadable package index must install the saved .deb files")
    for forbidden in ("download_packages", "apt-get update", "--download-only", "install_offline", "copy_cached_debs"):
        if forbidden in body:
            raise ContractError(f"an unreadable package index must not reach apt via {forbidden}")
    installer = _function_body(script, "install_saved_debs")
    if "dpkg -i" not in installer:
        raise ContractError("a saved .deb set must be installed with dpkg, not apt package names")
    if "apt-get" in installer:
        raise ContractError("a saved .deb set must not ask apt to resolve package names")


def _require_index_fallback(steps: list, exact: dict, install: dict) -> None:
    if exact.get("if") != "steps.resolve.outputs.index == 'ok'":
        raise ContractError("the versioned cache lookup runs only after the package index is readable")
    index_state = str((install.get("env") or {}).get("GDAL_INDEX_STATE") or "")
    if "steps.resolve.outputs.index" not in index_state:
        raise ContractError("install must know whether the package index was readable")
    matches = [step for step in steps if isinstance(step, dict) and step.get("id") == "gdal-debs-fallback"]
    if len(matches) != 1:
        raise ContractError("an unreadable package index must restore the newest saved .deb set")
    fallback = matches[0]
    if fallback.get("uses") != RESTORE_ACTION:
        raise ContractError("the saved-image restore must not save a new cache entry")
    if fallback.get("if") != "steps.resolve.outputs.index != 'ok'":
        raise ContractError("the saved-image restore runs only when the package index is unreadable")
    restore_keys = str((fallback.get("with") or {}).get("restore-keys") or "")
    for piece in ("runner.os", "runner.arch", "steps.resolve.outputs.image"):
        if piece not in restore_keys:
            raise ContractError(f"the saved-image restore must be limited to this runner image ({piece})")
    if "steps.resolve.outputs.versions" in restore_keys:
        raise ContractError("the saved-image restore cannot require the version the index could not build")


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
    _require_offline_hit(script)
    worst_case = provisioning_worst_case_seconds(script)
    _require_cache_action(action)
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
        (job_header + "75\n", job_header + "60\n", "job-timeout", 1),
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
        (
            'sudo timeout --kill-after=15s "${duration}" "$@"',
            'sudo "$@"',
            "unbounded-download",
            1,
        ),
        (
            "timeout --kill-after=15s 600s dpkg -i",
            "dpkg -i",
            "unbounded-dpkg",
            1,
        ),
        (
            "apt-get update --error-on=any -q",
            "apt-get update -q",
            "update-ignores-fetch-errors",
            -1,
        ),
        (
            "sudo timeout --kill-after=15s 60s apt-get install --print-uris",
            "sudo apt-get install --print-uris",
            "unbounded-resolve",
            1,
        ),
        ("for attempt in 1 2 3; do", "for attempt in 1 2 3 4 5 6; do", "too-many-attempts", -1),
        ("'Acquire::http::Timeout \"30\";'", "'Acquire::Queue-Mode \"host\";'", "no-http-timeout", 1),
        ("apt-get install -yqq --no-download", "apt-get install -yqq", "install-reaches-network", 1),
        (
            '    saved-image)\n      install_saved_debs "$cache_dir"\n',
            '    saved-image)\n      download_packages\n',
            "saved-image-downloads",
            1,
        ),
        (
            '    hit)\n      copy_cached_debs "$cache_dir"\n      install_offline\n',
            '    hit)\n      download_packages\n',
            "hit-downloads",
            1,
        ),
        (
            'export PATH="${installed_tools}"',
            'export PATH="/usr/bin:${PATH}"',
            "lookup-uses-inherited-path",
            1,
        ),
    )


def _action_breaks() -> tuple[Change, ...]:
    return (
        ("${{ steps.resolve.outputs.versions }}", "${{ runner.os }}", "cache-key-ignores-versions", 1),
        ("if: steps.resolve.outputs.index == 'ok'\n", "if: success()\n", "versioned-cache-without-index", 1),
        (
            "uses: actions/cache/restore@55cc8345863c7cc4c66a329aec7e433d2d1c52a9 # v6.1.0\n",
            "uses: actions/cache@55cc8345863c7cc4c66a329aec7e433d2d1c52a9 # v6.1.0\n",
            "fallback-saves",
            1,
        ),
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
        (ACTION_SOURCE, _action_breaks()),
        (WORKER_SOURCE, _worker_breaks()),
    )
    return tuple(_patch(sources, index, change) for index, changes in groups for change in changes)


def shell_self_test() -> None:
    completed = subprocess.run(
        ["bash", str(ROOT / "scripts/ci/provision-gdal-cli.sh"), "self-test"],
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
    shell_self_test()
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
