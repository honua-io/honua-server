#!/usr/bin/env python3
"""Assert the real-GDAL proof's apt provisioning stays bounded and outside the proof.

The `Prove the real-GDAL CLI cases run on the required gate` step used to
`apt-get update` / `apt-get install` inside its own 10-minute budget. When the
Azure Ubuntu mirror stalled, the step printed nothing and timed out (13 runs on
2026-10-23..26), and the import/export fidelity upload then failed with
"No files were found", hiding the real failure behind a second one.

This holds the fix structurally:

- the proof step installs nothing, keeps its 10-minute timeout, and stays
  blocking;
- a provisioning step runs before it under the same condition, with apt's own
  network retries and timeouts, every attempt wrapped in `timeout`, a bounded
  retry loop with backoff, and an offline (`--no-download`) install;
- the provisioning step's timeout covers the loop's worst case, and the job's
  timeout covers that plus the job's observed runtime;
- the fidelity evidence upload only errors on missing files when the fidelity
  proof actually ran.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path


JOB = "pr-gate-tests"
PROOF = "Prove the real-GDAL CLI cases run on the required gate"
PROVISION = "Provision the GDAL CLI packages for the real-GDAL proof"
FIDELITY = "Execute import and independent vector export fidelity proofs"
FIDELITY_UPLOAD = "Upload import and independent vector export fidelity evidence"
PROOF_TIMEOUT_MINUTES = 10
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
    """Worst case of the bounded download loop plus the offline install."""
    loop = re.search(r"for attempt in ([\d ]+); do(.*?)\n\s*done", script, re.DOTALL)
    if not loop:
        raise ContractError(f"{PROVISION!r} must retry the download in a bounded `for attempt in ...` loop")
    attempts = [int(value) for value in loop.group(1).split()]
    body = loop.group(2)
    bounded = re.search(rf"timeout --kill-after={DURATION} {DURATION} ", body)
    if not bounded or "apt-get update" not in body or "--download-only" not in body:
        raise ContractError(
            f"each {PROVISION!r} attempt must run apt-get update and a --download-only install under `timeout`"
        )
    per_attempt = int(bounded.group(1)) + int(bounded.group(2))
    backoff = re.search(r"sleep \$\(\(attempt \* (\d+)\)\)", body)
    if not backoff:
        raise ContractError(f"{PROVISION!r} must back off between attempts")
    # The loop does not sleep after its final attempt.
    sleeps = sum(attempt * int(backoff.group(1)) for attempt in attempts[:-1])
    install = re.search(
        rf"timeout --kill-after={DURATION} {DURATION} apt-get install [^\n]*--no-download", script
    )
    if not install:
        raise ContractError(
            f"{PROVISION!r} must finish with a bounded, offline (--no-download) apt-get install"
        )
    return len(attempts) * per_attempt + sleeps + int(install.group(1)) + int(install.group(2))


def validate(source: str) -> None:
    import yaml

    document = yaml.safe_load(source)
    job = ((document or {}).get("jobs") or {}).get(JOB)
    if not isinstance(job, dict) or not isinstance(job.get("steps"), list):
        raise ContractError(f"the required workflow no longer declares the {JOB!r} job's steps")
    steps = job["steps"]

    proof_index, proof = _step(steps, PROOF)
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
    if "dotnet test" not in proof_run or "Category=GDAL" not in proof_run:
        raise ContractError(f"{PROOF!r} no longer runs the Category=GDAL tests")

    provision_index, provision = _step(steps, PROVISION)
    if provision_index >= proof_index:
        raise ContractError(f"{PROVISION!r} must run before {PROOF!r}")
    if provision.get("if") != proof.get("if"):
        raise ContractError(f"{PROVISION!r} must run under exactly the same condition as {PROOF!r}")
    if "continue-on-error" in provision:
        raise ContractError(f"{PROVISION!r} must stay blocking")
    script = str(provision.get("run") or "")
    for option in ("Acquire::Retries", "Acquire::http::Timeout", "Acquire::https::Timeout"):
        if option not in script:
            raise ContractError(f"{PROVISION!r} must configure apt's {option}")

    worst_case = provisioning_worst_case_seconds(script)
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


def self_test() -> None:
    """Prove each rejection fires, so a green run means something."""
    source = read_workflow(Path(__file__).resolve().parents[3] / ".github/workflows/pr-gate.yml")
    validate(source)
    proof_anchor = "          export PATH=\"/usr/bin:${PATH}\"\n"
    injections = (
        # Provisioning moved back inside the proof.
        source.replace(proof_anchor, "          sudo apt-get update -qq\n" + proof_anchor, 1),
        # The proof's budget changed.
        source.replace(
            f"      - name: {PROOF}\n",
            f"      - name: {PROOF}\n        continue-on-error: true\n",
            1,
        ),
        # Unbounded attempts.
        source.replace("sudo timeout --kill-after=15s 240s sh -c", "sudo sh -c", 1),
        # More retries than the step timeout holds.
        source.replace("for attempt in 1 2 3; do", "for attempt in 1 2 3 4 5 6; do", 1),
        # apt's own network timeout dropped.
        source.replace("'Acquire::http::Timeout \"30\";'", "'Acquire::Queue-Mode \"host\";'", 1),
        # Install reaching the network again.
        source.replace("apt-get install -yqq --no-download", "apt-get install -yqq", 1),
        # The job can no longer outlive the provisioning budget.
        source.replace(
            "    name: PR Gate / Tests and runtime proofs\n    needs: pr-gate\n    runs-on: ubuntu-latest\n    timeout-minutes: 75\n",
            "    name: PR Gate / Tests and runtime proofs\n    needs: pr-gate\n    runs-on: ubuntu-latest\n    timeout-minutes: 60\n",
            1,
        ),
        # The upload masks the real failure again.
        source.replace(
            "if-no-files-found: ${{ steps.fidelity-proof.outcome != 'skipped' && 'error' || 'warn' }}",
            "if-no-files-found: error",
            1,
        ),
    )
    for index, candidate in enumerate(injections):
        if candidate == source:
            raise SystemExit(f"apt provisioning failure-injection fixture {index} no longer applies")
        try:
            validate(candidate)
        except ContractError:
            continue
        raise SystemExit(f"apt provisioning failure-injection fixture {index} was accepted")
    print("pr-gate-apt-provisioning-self-test=ok")


def read_workflow(path: Path) -> str:
    return path.read_text(encoding="utf-8").replace("\r\n", "\n")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "workflow",
        type=Path,
        nargs="?",
        default=Path(__file__).resolve().parents[3] / ".github/workflows/pr-gate.yml",
    )
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    try:
        validate(read_workflow(args.workflow))
    except ContractError as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1
    if args.self_test:
        self_test()
    print("pr-gate-apt-provisioning=ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
