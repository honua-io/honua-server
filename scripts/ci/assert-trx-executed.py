#!/usr/bin/env python3
"""Counts tripwire for TRX-producing certification lanes (honua-server#4414).

`dotnet test` exits 0 whether every selected case ran or every selected case
skipped.  Every cell in the cloud lanes is a `[SkippableFact]` / `[CloudTest]`
that *skips* rather than fails when its inputs are absent, so the exit code
alone cannot tell an all-passed run from an all-skipped one.  This script reads
the TRX the lane already produces and turns "nothing executed" into a failure,
naming the cells that skipped.

It is the same idiom as `ci.yml`'s Worker GDAL tripwire and the
`Category=LocalSubstrate` tripwire in `cloud-integration-harness.yml`, factored
out so every lane asserts it the same way.

Usage:
    assert-trx-executed.py --label "Real-AWS certification" \
        --trx tests/TestResults/real-aws-certification.trx \
        --min-passed 1

    assert-trx-executed.py --self-test

Exit status is 0 only when the TRX exists, at least `--min-passed` cases
passed, no case failed, and no more than `--max-skipped` cases skipped.
"""

from __future__ import annotations

import argparse
import os
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

# Outcomes vstest writes into a TRX. "NotExecuted" is what a Skip.If / CloudTest
# skip produces, which is the outcome this tripwire exists to notice.
PASSED = "Passed"
FAILED = "Failed"
SKIPPED = "NotExecuted"


class TripwireError(RuntimeError):
    """A lane-level failure the caller should report and exit non-zero on."""


def _local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def read_results(trx_path: Path) -> list[tuple[str, str]]:
    """Return [(testName, outcome)] for every UnitTestResult in the TRX."""
    if not trx_path.is_file():
        raise TripwireError(f"no TRX was produced at {trx_path}")

    try:
        root = ET.parse(trx_path).getroot()
    except ET.ParseError as exc:  # pragma: no cover - exercised by --self-test
        raise TripwireError(f"{trx_path} is not parseable TRX: {exc}") from exc

    results: list[tuple[str, str]] = []
    for node in root.iter():
        if _local_name(node.tag) != "UnitTestResult":
            continue
        results.append(
            (
                node.attrib.get("testName", "<unnamed>"),
                node.attrib.get("outcome", "<no outcome>"),
            )
        )
    return results


def summarise(results: list[tuple[str, str]]) -> dict[str, int]:
    counts: dict[str, int] = {}
    for _, outcome in results:
        counts[outcome] = counts.get(outcome, 0) + 1
    return counts


def render_summary(label: str, results: list[tuple[str, str]]) -> str:
    counts = summarise(results)
    lines = [
        f"### {label} — executed-case counts",
        "",
        f"- Passed: {counts.get(PASSED, 0)}",
        f"- Failed: {counts.get(FAILED, 0)}",
        f"- Skipped: {counts.get(SKIPPED, 0)}",
        f"- Total cases in TRX: {len(results)}",
    ]
    other = {
        outcome: count
        for outcome, count in sorted(counts.items())
        if outcome not in (PASSED, FAILED, SKIPPED)
    }
    for outcome, count in other.items():
        lines.append(f"- {outcome}: {count}")

    skipped = [name for name, outcome in results if outcome == SKIPPED]
    if skipped:
        lines += ["", "Skipped cells (non-passing evidence):", ""]
        lines += [f"- `{name}`" for name in sorted(skipped)]
    lines.append("")
    return "\n".join(lines)


def check(
    label: str,
    trx_paths: list[Path],
    min_passed: int,
    max_skipped: int | None,
) -> str:
    """Evaluate the tripwire, returning the step-summary markdown.

    Raises TripwireError when the lane did not prove what it claims to.
    """
    if min_passed < 1 or (max_skipped is not None and max_skipped < 0):
        raise TripwireError("min-passed must be positive and max-skipped must be non-negative")

    results: list[tuple[str, str]] = []
    for path in trx_paths:
        results.extend(read_results(path))

    summary = render_summary(label, results)
    counts = summarise(results)
    passed = counts.get(PASSED, 0)
    failed = counts.get(FAILED, 0)
    skipped = counts.get(SKIPPED, 0)

    problems: list[str] = []
    if passed < min_passed:
        problems.append(
            f"{label}: only {passed} case(s) passed but at least {min_passed} must execute — "
            f"{skipped} case(s) skipped, so this run is NOT evidence that the lane works. "
            "An all-skipped run is not an all-passed run."
        )
    if failed > 0:
        problems.append(f"{label}: {failed} case(s) failed.")
    for outcome, count in sorted(counts.items()):
        if outcome not in (PASSED, FAILED, SKIPPED):
            problems.append(f"{label}: {count} case(s) have non-passing outcome {outcome!r}.")
    if max_skipped is not None and skipped > max_skipped:
        problems.append(
            f"{label}: {skipped} case(s) skipped but at most {max_skipped} may skip in this lane."
        )

    if problems:
        raise TripwireError("\n".join(problems) + "\n\n" + summary)
    return summary


def emit(summary: str) -> None:
    print(summary)
    step_summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if step_summary:
        with open(step_summary, "a", encoding="utf-8") as handle:
            handle.write(summary + "\n")


def _trx(results: list[tuple[str, str]]) -> str:
    body = "".join(
        f'<UnitTestResult testName="{name}" outcome="{outcome}" />'
        for name, outcome in results
    )
    return (
        '<?xml version="1.0" encoding="UTF-8"?>'
        '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
        f"<Results>{body}</Results></TestRun>"
    )


def self_test() -> int:
    """Prove the tripwire fires on the shapes the cloud lanes actually produce."""
    failures: list[str] = []

    def expect_ok(name: str, fn) -> None:
        try:
            fn()
        except TripwireError as exc:
            failures.append(f"{name}: expected pass, got failure: {exc}")

    def expect_fail(name: str, fn, needle: str) -> None:
        try:
            fn()
        except TripwireError as exc:
            if needle not in str(exc):
                failures.append(f"{name}: failure message missing {needle!r}: {exc}")
        else:
            failures.append(f"{name}: expected failure, got pass")

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)

        all_skipped = root / "all-skipped.trx"
        all_skipped.write_text(
            _trx([("CellA", SKIPPED), ("CellB", SKIPPED)]), encoding="utf-8"
        )
        mixed = root / "mixed.trx"
        mixed.write_text(
            _trx([("CellA", PASSED), ("CellB", SKIPPED)]), encoding="utf-8"
        )
        all_passed = root / "all-passed.trx"
        all_passed.write_text(
            _trx([("CellA", PASSED), ("CellB", PASSED)]), encoding="utf-8"
        )
        with_failure = root / "failed.trx"
        with_failure.write_text(
            _trx([("CellA", PASSED), ("CellB", FAILED)]), encoding="utf-8"
        )

        # The defect this exists for: dotnet test exits 0 on an all-skipped run.
        expect_fail(
            "all-skipped run is rejected",
            lambda: check("lane", [all_skipped], 1, None),
            "An all-skipped run is not an all-passed run",
        )
        expect_ok("all-passed run is accepted", lambda: check("lane", [all_passed], 1, None))
        expect_ok(
            "partially-skipped run is accepted when skips are allowed",
            lambda: check("lane", [mixed], 1, None),
        )
        expect_fail(
            "partially-skipped run is rejected under --max-skipped 0",
            lambda: check("lane", [mixed], 1, 0),
            "at most 0 may skip",
        )
        expect_fail(
            "a failing case is rejected",
            lambda: check("lane", [with_failure], 1, None),
            "1 case(s) failed",
        )
        for outcome in ("Error", "Aborted", "Timeout", "Inconclusive", "<no outcome>"):
            incomplete = root / "incomplete.trx"
            incomplete.write_text(
                _trx([("CellA", PASSED), ("CellB", outcome.replace("<", "&lt;"))]),
                encoding="utf-8",
            )
            expect_fail(
                f"non-passing outcome {outcome} is rejected",
                lambda: check("lane", [incomplete], 1, None),
                "non-passing outcome",
            )
        expect_fail(
            "zero execution floor is rejected",
            lambda: check("lane", [all_skipped], 0, None),
            "min-passed must be positive",
        )
        expect_fail(
            "a missing TRX is rejected",
            lambda: check("lane", [root / "absent.trx"], 1, None),
            "no TRX was produced",
        )
        expect_fail(
            "a min-passed floor above the executed count is rejected",
            lambda: check("lane", [all_passed], 3, None),
            "at least 3 must execute",
        )
        # Multiple TRX files aggregate.
        expect_ok(
            "multiple TRX inputs aggregate",
            lambda: check("lane", [all_skipped, all_passed], 2, None),
        )
        # Skipped cells are named so the operator can see which evidence is missing.
        rendered = render_summary("lane", read_results(mixed))
        if "`CellB`" not in rendered:
            failures.append("skipped cells are not named in the summary")
        if "- Passed: 1" not in rendered:
            failures.append("summary does not report the passed count")

    for failure in failures:
        print(f"FAIL: {failure}", file=sys.stderr)
    if failures:
        print(f"{len(failures)} self-test assertion(s) failed.", file=sys.stderr)
        return 1
    print("assert-trx-executed self-test: all assertions passed.")
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--label", default="lane", help="Lane name used in messages.")
    parser.add_argument(
        "--trx",
        action="append",
        default=[],
        help="Path to a TRX file. Repeatable; counts are aggregated.",
    )
    parser.add_argument(
        "--min-passed",
        type=int,
        default=1,
        help="Fail when fewer than this many cases passed (default: 1).",
    )
    parser.add_argument(
        "--max-skipped",
        type=int,
        default=None,
        help="Fail when more than this many cases skipped (default: no ceiling).",
    )
    parser.add_argument("--self-test", action="store_true", help="Run the self-test.")
    args = parser.parse_args(argv)

    if args.self_test:
        return self_test()
    if not args.trx:
        parser.error("--trx is required unless --self-test is given")

    try:
        emit(check(args.label, [Path(p) for p in args.trx], args.min_passed, args.max_skipped))
    except TripwireError as exc:
        first_line = str(exc).splitlines()[0]
        print(f"::error::{first_line}", file=sys.stderr)
        print(str(exc), file=sys.stderr)
        step_summary = os.environ.get("GITHUB_STEP_SUMMARY")
        if step_summary:
            with open(step_summary, "a", encoding="utf-8") as handle:
                handle.write(f"> [!CAUTION]\n> {first_line}\n\n")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
