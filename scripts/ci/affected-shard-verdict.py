#!/usr/bin/env python3
"""Publish the PR Gate affected-shard aggregate.

`report` concludes success so a red shard does not mark the pull request
UNSTABLE, but that success is not a test pass. `test_pass` is true only when
`AFFECTED_SHARDS_MODE` is `enforce` and every selected shard passed. Promotion
stays the two audited changes in pr-gate.yml; a required branch-protection
entry does not promote this lane, and a green required check in report mode
does not either.

Missing evidence fails closed only in `enforce`. A partial receipt set is not
green. A confirmed red is reported as a red even when some families produced
no receipt. Annotation text is part of the promotion auditor's contract
(`HONUA_AFFECTED_SHARD_RED` / `_GREEN` / `_UNAVAILABLE`).
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path
from typing import Any

CONTRACT = "honua.pr-gate-affected-shard-verdict/v1"
RED_ANNOTATION = (
    "HONUA_AFFECTED_SHARD_RED shard='{shard}' status='{status}' — "
    "this shard family is red on the pull-request head. The trailing matrix "
    "will report the same failure on trunk after this merges."
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


def _cell(value: object) -> str:
    if value is None:
        return "null"
    return str(value)


def decide(
    *,
    mode: str,
    select_result: str,
    select_skip: str,
    select_reason: str,
    select_count: str,
    shards_result: str,
    timings: list[dict[str, Any]],
) -> dict[str, Any]:
    """Return the aggregate verdict. Exit 0 in report mode is not a test pass."""
    enforcing = mode == "enforce"
    lines = ["### PR Gate / Affected shards", ""]
    if enforcing:
        lines.append(f"- Mode: `{mode}` (enforcing; see the promotion rule in pr-gate.yml)")
    else:
        lines.append(
            f"- Mode: `{mode or 'report'}` "
            "(report-only until the promotion rule in pr-gate.yml is met)"
        )
    lines.append(
        f"- Selection: `{select_reason or 'unavailable'}` (`{select_count or '0'}` shard families)"
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

    if select_result != "success":
        return unavailable(
            f"selection_{select_result}",
            "no shard evidence for this head.",
            f"selection concluded '{select_result}'.",
        )
    if select_skip == "true":
        return finish(
            "skipped",
            exit_code=0,
            shards_passed=False,
            extra=["- Verdict: `skipped` — nothing in this diff can move a server-test shard."],
        )

    counts["expected"] = _count(select_count)
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
        "| Shard | Status | Duration | Budget used | Capacity |",
        "|---|---|---:|---:|---|",
    ])
    for timing in timings:
        shard = _cell(timing.get("shard"))
        status = _cell(timing.get("status"))
        duration = _cell(timing.get("duration_seconds"))
        lines.append(
            f"| {shard} | `{status}` | {duration}s | {_budget(timing.get('headroom_ratio'))} | "
            f"`{_cell(timing.get('capacity_status'))}` |"
        )
        if status != "passed":
            counts["red"] += 1
            annotations.append(("error", RED_ANNOTATION.format(shard=shard, status=status)))

    lines.append("")
    received = counts["received"]
    expected = counts["expected"]
    red = counts["red"]
    if red == 0 and received < expected:
        return unavailable(
            "incomplete_receipts",
            f"only {received} of {expected} selected shard families produced a timing receipt.",
            f"shard jobs concluded '{shards_result}' with {received}/{expected} timing receipts.",
            "Every receipt received says `passed`, but the families that reported none are "
            "exactly the ones this lane spent runners to learn about, so this head has no "
            "complete shard evidence.",
        )
    if red == 0:
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
            f"PR Gate / Affected shards: {red} shard families are red on this head.",
        ))
        return finish("red", exit_code=1, shards_passed=False, extra=extra)
    extra.extend([
        "  Reported only: `AFFECTED_SHARDS_MODE` is `report`, so this context stays green while",
        "  the false-red rate is measured. Treat the rows above as a pre-merge prediction of the",
        "  trailing matrix and fix them before landing.",
    ])
    return finish("red", exit_code=0, shards_passed=False, extra=extra)


def _load_timings(directory: Path) -> list[dict[str, Any]]:
    if not directory.is_dir():
        return []
    timings: list[dict[str, Any]] = []
    for path in sorted(directory.glob("*.timing.json")):
        value = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(value, dict):
            raise ValueError(f"{path.name} is not a timing object")
        timings.append(value)
    return timings


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--timings", type=Path, required=True)
    parser.add_argument("--receipt", type=Path, required=True)
    args = parser.parse_args(argv)
    outcome = decide(
        mode=os.environ.get("MODE", ""),
        select_result=os.environ.get("SELECT_RESULT", ""),
        select_skip=os.environ.get("SELECT_SKIP", ""),
        select_reason=os.environ.get("SELECT_REASON", ""),
        select_count=os.environ.get("SELECT_COUNT", ""),
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
