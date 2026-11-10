#!/usr/bin/env python3
"""Publish and enforce the existing selected-shard execution receipts."""

from __future__ import annotations

import argparse
import json
import os
import runpy
import sys
from pathlib import Path
from typing import Any, NamedTuple

CONTRACT = "honua.pr-gate-affected-shard-verdict/v1"
UNAFFECTED_SKIP_REASONS = {
    "docs_only", "validated_generated_only", "no_product_code", "no_shards_selected",
}
DEFERRED_SKIP_REASON = "affected_shards_deferred_to_trunk"
observe_execution = runpy.run_path(str(Path(__file__).with_name("shard-execution-evidence.py")))["observe"]
RED_ANNOTATION = (
    "HONUA_AFFECTED_SHARD_RED shard='{shard}' status='{status}' — "
    "this shard family has failing execution evidence for this run."
)


def _budget(ratio: object) -> str:
    if isinstance(ratio, bool) or not isinstance(ratio, (int, float)):
        return "n/a"
    # jq: ((.headroom_ratio * 1000 | round) / 10 | tostring). round is half away
    # from zero. Drop a trailing .0 so 12 prints as 12, matching jq.
    scaled = int(float(ratio) * 1000 + (0.5 if ratio >= 0 else -0.5)) / 10
    text = f"{scaled:.1f}"
    if text.endswith(".0"):
        text = text[:-2]
    return f"{text}%"


def _count(value: str) -> int:
    text = value.strip()
    if text == "":
        return 0
    if not text.isdecimal():
        raise ValueError(f"selected shard count is invalid: {value!r}")
    return int(text)


def _names(value: str) -> tuple[str, ...]:
    names = json.loads(value)
    if not isinstance(names, list) or not all(isinstance(name, str) and name for name in names):
        raise ValueError(f"deferred shard list is invalid: {value!r}")
    return tuple(names)


def _cell(value: object) -> str:
    if value is None:
        return "null"
    return str(value)


class Selection(NamedTuple):
    result: str
    skip: str
    reason: str
    count: str
    shards: list[str]
    deferred: tuple[str, ...] = ()


def decide(
    *,
    mode: str,
    selection: Selection,
    shards_result: str,
    timings: list[dict[str, Any]],
) -> dict[str, Any]:
    """Return the aggregate verdict. Exit 0 in report mode is not a test pass."""
    enforcing = mode == "enforce"
    lines = ["### Server test shards", ""]
    if enforcing:
        lines.append(f"- Mode: `{mode}` (enforcing; selected execution receipts gate merging)")
    else:
        lines.append(
            f"- Mode: `{mode or 'report'}` "
            "(report-only; enforcement policy: docs/ci/affected-shards-promotion.md)"
        )
    lines.append(
        f"- Selection: `{selection.reason or 'unavailable'}` (`{selection.count or '0'}` shard families)"
    )
    annotations: list[tuple[str, str]] = []
    counts = {"received": 0, "expected": 0, "red": 0}

    def finish(
        verdict: str,
        *,
        exit_code: int,
        shards_passed: bool,
        extra: list[str] | None = None,
    ) -> dict[str, Any]:
        # Report-mode check success publishes the verdict. It is never a test
        # pass, including when every received shard passed or the diff selected
        # nothing. Enforce mode is a test pass only for a complete green table.
        test_pass = enforcing and verdict == "green"
        if extra:
            lines.extend(extra)
        lines.extend(deferred_lines(selection.deferred))
        lines.append(f"- Test pass: `{str(test_pass).lower()}`")
        if not enforcing:
            lines.append(
                "- Check success in report mode publishes this verdict. "
                "It is not a test pass and it is not promotion."
            )
            annotations.append((
                "notice",
                "HONUA_AFFECTED_SHARD_REPORT_ONLY "
                f"test_pass=false verdict={verdict} shards_passed={str(shards_passed).lower()}",
            ))
        elif not test_pass:
            lines.append(
                "- Test pass is false: this context did not see a complete green shard table."
            )
        receipt = {
            "contract": CONTRACT,
            "mode": "enforce" if enforcing else "report",
            "verdict": verdict,
            "test_pass": test_pass,
            "shards_passed": shards_passed,
            "check_exit": exit_code,
            "received": counts["received"],
            "expected": counts["expected"],
            "red": counts["red"],
        }
        return {
            "exit_code": exit_code,
            "summary": "\n".join(lines) + "\n",
            "annotations": annotations,
            "receipt": receipt,
        }

    def unavailable(verdict: str, explanation: str, annotation: str, detail: str = "") -> dict[str, Any]:
        extra = [f"- Verdict: `{verdict}` — {explanation}"]
        if detail:
            extra.append(f"  {detail}")
        if enforcing:
            extra.append(
                "  `AFFECTED_SHARDS_MODE` is `enforce`, so this required context fails closed on"
            )
            extra.append("  missing evidence rather than reporting a pass it cannot support.")
            annotations.append(("error", f"HONUA_AFFECTED_SHARD_UNAVAILABLE {annotation}"))
            return finish(verdict, exit_code=1, shards_passed=False, extra=extra)
        annotations.append(("warning", f"HONUA_AFFECTED_SHARD_UNAVAILABLE {annotation}"))
        return finish(verdict, exit_code=0, shards_passed=False, extra=extra)

    if selection.result != "success":
        return unavailable(
            f"selection_{selection.result}",
            "no shard evidence for this head.",
            f"selection concluded '{selection.result}'.",
        )
    if selection.skip == "true" and selection.reason == DEFERRED_SKIP_REASON:
        if not selection.deferred or _count(selection.count) != 0 or selection.shards:
            return unavailable(
                "untrusted_skip", "a deferred selection must name its deferred shards and select none.",
                f"selection skipped with reason '{selection.reason}'.",
            )
        annotations.append((
            "notice",
            f"HONUA_AFFECTED_SHARD_DEFERRED {len(selection.deferred)} shard families deferred to trunk.",
        ))
        return finish(
            "deferred",
            exit_code=0,
            shards_passed=False,
            extra=[
                "- Verdict: `deferred` — every affected shard family is over the PR time budget; "
                "the post-merge trunk run covers them.",
            ],
        )
    if selection.skip == "true":
        if enforcing and (selection.reason not in UNAFFECTED_SKIP_REASONS
                          or _count(selection.count) != 0 or selection.shards):
            return unavailable(
                "untrusted_skip", "selection cannot prove that no shard is affected.",
                f"selection skipped with reason '{selection.reason}'.",
            )
        return finish(
            "skipped",
            exit_code=0,
            shards_passed=False,
            extra=["- Verdict: `skipped` — nothing in this diff can move a server-test shard."],
        )

    counts["expected"] = _count(selection.count)
    counts["received"] = len(timings)
    if counts["received"] == 0:
        return unavailable(
            "no_receipts",
            f"shard jobs concluded `{shards_result}` without a timing artifact.",
            f"shard jobs concluded '{shards_result}' with no timing receipt.",
            "A shard that fails before its test step (restore, build, runner disk) produces "
            "no receipt; that is an infrastructure failure of this lane, not a shard regression.",
        )

    lines.extend([
        "",
        "| Shard | Status | Duration | Budget used | Capacity | Passed | Failed | Skipped | Executed |",
        "|---|---|---:|---:|---|---:|---:|---:|---:|",
    ])
    for timing in timings:
        shard = _cell(timing.get("shard"))
        status = _cell(timing.get("status"))
        duration = _cell(timing.get("duration_seconds"))
        cases = (timing.get("execution") or {}).get("counts") or {}
        case_cells = " | ".join(str(cases.get(key, "n/a")) for key in ("passed", "failed", "skipped", "executed"))
        lines.append(
            f"| {shard} | `{status}` | {duration}s | {_budget(timing.get('headroom_ratio'))} | "
            f"`{_cell(timing.get('capacity_status'))}` | {case_cells} |"
        )
        if status != "passed":
            counts["red"] += 1
            annotations.append(("error", RED_ANNOTATION.format(shard=shard, status=status)))

    lines.append("")
    received = counts["received"]
    expected = counts["expected"]
    red = counts["red"]
    received_shards = [timing.get("shard") for timing in timings]
    complete = expected == len(selection.shards) and sorted(received_shards) == sorted(selection.shards)
    if red == 0 and not complete:
        return unavailable(
            "incomplete_receipts",
            f"receipts do not match the {expected} selected shard families ({received} received).",
            f"shard jobs concluded '{shards_result}' with {received}/{expected} timing receipts.",
            "Require exactly one receipt for each selected family; duplicates and unrelated receipts "
            "cannot substitute for missing evidence.",
        )
    if red == 0:
        if shards_result != "success":
            return unavailable(
                "shard_jobs_not_successful",
                f"execution receipts are green but shard jobs concluded `{shards_result}`.",
                f"shard jobs concluded '{shards_result}'.",
            )
        annotations.append((
            "notice",
            f"HONUA_AFFECTED_SHARD_GREEN {received} selected shard families passed.",
        ))
        return finish(
            "green",
            exit_code=0,
            shards_passed=True,
            extra=[
                f"- Verdict: `green` — all {received} selected shard families passed on this head."
            ],
        )

    extra = [f"- Verdict: `red` — {red} of {received} selected shard families failed on this head."]
    if enforcing:
        annotations.append((
            "error",
            f"Server test shards: {red} shard families are red on this head.",
        ))
        return finish("red", exit_code=1, shards_passed=False, extra=extra)
    extra.extend([
        "  Reported only: `AFFECTED_SHARDS_MODE` is `report`, so this context stays green while",
        "  the false-red rate is measured. Treat the rows above as a pre-merge prediction of the",
        "  trailing matrix and fix them before landing.",
    ])
    return finish("red", exit_code=0, shards_passed=False, extra=extra)


def deferred_lines(deferred: tuple[str, ...]) -> list[str]:
    if not deferred:
        return []
    return ["- Deferred to the post-merge trunk run (over the PR time budget):"] + [
        f"  - {name}" for name in deferred
    ]


def _verify_execution(timing: dict[str, Any], directory: Path) -> None:
    execution = timing.get("execution") or {}
    trx_name = f"{timing.get('log_name')}.trx"
    if Path(trx_name).name != trx_name or execution.get("trxFile") != trx_name:
        if timing.get("status") == "passed":
            timing["status"] = "invalid_execution"
        timing["execution"] = {}
        return
    measured = observe_execution(directory / trx_name, timing.get("shard"), execution.get("project"))
    if (execution.get("status") != "passed" or measured["status"] != "passed"
            or execution.get("trxSha256") != measured["trxSha256"]
            or execution.get("counts") != measured["counts"]):
        if timing.get("status") == "passed":
            timing["status"] = "invalid_execution"
    timing["execution"] = measured


def _load_timings(directory: Path) -> list[dict[str, Any]]:
    if not directory.is_dir():
        return []
    timings: list[dict[str, Any]] = []
    for path in sorted(directory.glob("*.timing.json")):
        value = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(value, dict):
            raise ValueError(f"{path.name} is not a timing object")
        _verify_execution(value, directory)
        timings.append(value)
    return timings


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--timings", type=Path, required=True)
    parser.add_argument("--receipt", type=Path, required=True)
    args = parser.parse_args(argv)
    outcome = decide(
        mode=os.environ.get("MODE", ""),
        selection=Selection(
            result=os.environ.get("SELECT_RESULT", ""),
            skip=os.environ.get("SELECT_SKIP", ""),
            reason=os.environ.get("SELECT_REASON", ""),
            count=os.environ.get("SELECT_COUNT", ""),
            shards=[entry["shard_name"] for entry in json.loads(os.environ.get("SELECT_MATRIX") or "[]")],
            deferred=_names(os.environ.get("SELECT_DEFERRED") or "[]"),
        ),
        shards_result=os.environ.get("SHARDS_RESULT", ""),
        timings=_load_timings(args.timings),
    )
    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_path:
        with open(summary_path, "a", encoding="utf-8") as handle:
            handle.write(outcome["summary"])
    for level, message in outcome["annotations"]:
        print(f"::{level}::{message}")
    args.receipt.write_text(
        json.dumps(outcome["receipt"], indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    return int(outcome["exit_code"])


if __name__ == "__main__":
    try:
        sys.exit(main())
    except ValueError as error:
        print(f"::error::affected-shard verdict input is invalid: {error}", file=sys.stderr)
        sys.exit(1)
