#!/usr/bin/env python3
"""Assert the always-on PR Gate steps stay unconditional.

Both steps read documents that the PR Gate impact classifier treats as safe or
as governed: `Verify review-first admission contract` parses
`docs/internal/ci/gate-model.md` and `docs/internal/ci/workflow-inventory.md`,
and `Verify .NET base-image security inventory` parses the code-scanning
inventory. Their soundness argument depends on running unconditionally, so this
asserts it structurally -- no step `if`, no `continue-on-error`, no job `if`,
and the step still invokes its script -- rather than by matching workflow text.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path


REQUIRED_STEPS = {
    "Verify review-first admission contract": "scripts/ci/validate-review-first-dispatch.sh",
    "Verify .NET base-image security inventory": "scripts/ci/base-image-mirrors.sh",
}
FORBIDDEN_KEYS = ("if", "continue-on-error")
JOB = "pr-gate"


class ContractError(Exception):
    """A structural violation of the always-on step contract."""


def validate_semantic(source: str) -> None:
    import yaml

    document = yaml.safe_load(source)
    if not isinstance(document, dict):
        raise ContractError("the required workflow is not a YAML mapping")
    job = (document.get("jobs") or {}).get(JOB)
    if not isinstance(job, dict):
        raise ContractError(f"the required workflow no longer declares the {JOB!r} job")
    if "if" in job:
        raise ContractError(
            f"the {JOB!r} job must not be conditional; a skipped required context blocks merge"
        )
    steps = job.get("steps")
    if not isinstance(steps, list):
        raise ContractError(f"the {JOB!r} job declares no steps")
    for name, script in REQUIRED_STEPS.items():
        matches = [step for step in steps if isinstance(step, dict) and step.get("name") == name]
        if len(matches) != 1:
            raise ContractError(
                f"expected exactly one always-on step named {name!r}; found {len(matches)}"
            )
        step = matches[0]
        for key in FORBIDDEN_KEYS:
            if key in step:
                raise ContractError(f"the always-on step {name!r} must not declare {key!r}")
        if script not in str(step.get("run") or ""):
            raise ContractError(f"the always-on step {name!r} no longer runs {script}")


def validate_textual(source: str) -> None:
    """Fallback for machines without PyYAML.

    Stricter than the semantic path: it accepts only the canonical unquoted
    step-name spelling this repository uses.
    """
    for name, script in REQUIRED_STEPS.items():
        anchor = f"\n      - name: {name}\n"
        if anchor not in source:
            raise ContractError(f"the required workflow no longer declares the step {name!r}")
        block: list[str] = []
        for line in source[source.index(anchor) + len(anchor) :].split("\n"):
            if line.startswith("      - ") or (line.strip() and not line.startswith("        ")):
                break
            block.append(line)
        body = "\n".join(block)
        for key in FORBIDDEN_KEYS:
            if re.search(rf"^        {re.escape(key)}\s*:", body, re.MULTILINE):
                raise ContractError(f"the always-on step {name!r} must not declare {key!r}")
        if script not in body:
            raise ContractError(f"the always-on step {name!r} no longer runs {script}")


def validate(source: str) -> None:
    try:
        import yaml  # noqa: F401
    except ModuleNotFoundError:
        validate_textual(source)
    else:
        validate_semantic(source)


def self_test() -> None:
    """Prove each rejection fires, so a green run means something."""
    source = read_workflow(Path(__file__).resolve().parents[3] / ".github/workflows/pr-gate.yml")
    validate(source)
    injections = (
        source.replace(
            "      - name: Verify review-first admission contract\n",
            "      - name: Verify review-first admission contract\n        if: github.run_attempt > 1\n",
            1,
        ),
        source.replace(
            "      - name: Verify .NET base-image security inventory\n",
            "      - name: Verify .NET base-image security inventory\n        continue-on-error: true\n",
            1,
        ),
        source.replace("scripts/ci/validate-review-first-dispatch.sh", "true", 1),
        source.replace("  pr-gate:\n", "  pr-gate:\n    if: false\n", 1),
    )
    for index, candidate in enumerate(injections):
        try:
            validate(candidate)
        except ContractError:
            continue
        raise SystemExit(f"always-on step failure-injection fixture {index} was accepted")
    print("pr-gate-always-on-steps-self-test=ok")


def read_workflow(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8").replace("\r\n", "\n")
    except UnicodeDecodeError as error:
        byte = error.object[error.start : error.start + 1]
        raise SystemExit(
            f"::error::{path}: not valid UTF-8 -- byte 0x{byte.hex()} at offset {error.start}"
        ) from error


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("workflow", type=Path)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    try:
        validate(read_workflow(args.workflow))
    except ContractError as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1
    if args.self_test:
        self_test()
    print("pr-gate-always-on-steps=ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
