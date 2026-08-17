#!/usr/bin/env python3
"""Guard: the [GdalCliFact] cases must actually EXECUTE, not skip (#3271).

`GdalCliFactAttribute` (tests/dotnet/Honua.Worker.Gdal.Tests/GdalCli.cs) sets
`Skip` in its constructor when the GDAL CLI tool it names is absent from PATH.
That is the right behaviour for a dev box, and it was also the behaviour on the
lean `ubuntu-latest` agent that used to run `Honua.Worker.Gdal.Tests` as one step
of `dotnet-foundation-tests`: every real-GDAL case reported "skipped" and the
step passed. The only coverage that exercises the actual `gdaldem` / `ogr2ogr`
command lines the native-profile worker shells out to was therefore green-by-
absence, and a regression in `ProcessGdalCommandRunner` or any
`Gdal*JobExecutor` argument projection would not have turned CI red.

Skipping is invisible in a `dotnet test` exit code, so the GDAL-capable job needs
an explicit assertion. This script:

  1. Enumerates the EXPECTED real-GDAL cases from the sources — every
     `[GdalCliFact(...)]` attribute, resolved to `<namespace>.<class>.<method>`,
     which is exactly the `testName` VSTest writes into the TRX.
  2. Reads the TRX the job produced and requires every expected case to be
     present AND to have run (outcome must not be `NotExecuted`).
  3. Fails when the expected set is EMPTY, so deleting or renaming the attribute
     cannot silently retire the guard along with the coverage.

Exit codes: 0 ok, 1 assertion failed, 2 usage/parse error.
"""

from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ATTRIBUTE = "GdalCliFact"
# `[GdalCliFact("gdaldem")]` ... optionally followed by more attribute blocks
# ([Protocol(...)], [Operation(...)]) before the method declaration itself.
_ATTR_RE = re.compile(rf"\[\s*{ATTRIBUTE}\s*\(")
_ATTR_BLOCK_RE = re.compile(r"\s*\[[^\]]*\]")
_METHOD_DECL_RE = re.compile(
    r"[^;{}()]*?\b([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^<>()]*>)?\s*\("
)
_NAMESPACE_RE = re.compile(r"^\s*namespace\s+([A-Za-z0-9_.]+)\s*;?\s*$", re.M)
_CLASS_RE = re.compile(
    r"^\s*(?:public|internal|sealed|abstract|static|partial|\s)*"
    r"(?:class|record)\s+([A-Za-z_][A-Za-z0-9_]*)",
    re.M,
)
# Outcomes VSTest writes for a case that never ran.
_NOT_RUN_OUTCOMES = frozenset({"NotExecuted", "Skipped", "None"})


def _method_name_after(text: str, index: int) -> str | None:
    """Return the method name declared after the attribute block at `index`."""
    pos = index
    while True:
        block = _ATTR_BLOCK_RE.match(text, pos)
        if not block:
            break
        pos = block.end()
    decl = _METHOD_DECL_RE.match(text, pos)
    return decl.group(1) if decl else None


def expected_cases(source_dir: Path) -> dict[str, str]:
    """Return {fully_qualified_test_name: source_file} for every [GdalCliFact]."""
    expected: dict[str, str] = {}
    for path in sorted(source_dir.rglob("*.cs")):
        text = path.read_text(encoding="utf-8", errors="replace")
        if ATTRIBUTE not in text:
            continue
        namespace_match = _NAMESPACE_RE.search(text)
        if not namespace_match:
            continue
        namespace = namespace_match.group(1)
        for match in _ATTR_RE.finditer(text):
            # The attribute's own declaration in GdalCli.cs is not a test.
            class_match = None
            for candidate in _CLASS_RE.finditer(text, 0, match.start()):
                class_match = candidate
            method = _method_name_after(text, match.start())
            if not class_match or not method:
                continue
            expected[f"{namespace}.{class_match.group(1)}.{method}"] = str(path)
    return expected


def trx_outcomes(trx_paths: list[Path]) -> dict[str, str]:
    outcomes: dict[str, str] = {}
    for path in trx_paths:
        root = ET.parse(path).getroot()
        for node in root.findall(".//{*}UnitTestResult"):
            name = node.attrib.get("testName")
            outcome = node.attrib.get("outcome")
            if name and outcome:
                outcomes[name] = outcome
    return outcomes


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--source-dir",
        type=Path,
        default=Path("tests/dotnet/Honua.Worker.Gdal.Tests"),
        help="directory scanned for [GdalCliFact] attributes",
    )
    parser.add_argument(
        "--trx", type=Path, action="append", required=True,
        help="TRX file produced by the GDAL-capable test run; repeatable",
    )
    parser.add_argument(
        "--summary-file", type=Path, default=None,
        help="append a markdown run/skip summary here (e.g. $GITHUB_STEP_SUMMARY)",
    )
    args = parser.parse_args()

    if not args.source_dir.is_dir():
        print(f"::error::source dir not found: {args.source_dir}", file=sys.stderr)
        return 2
    missing_trx = [path for path in args.trx if not path.is_file()]
    if missing_trx:
        print(
            "::error::TRX not found: "
            + ", ".join(str(path) for path in missing_trx),
            file=sys.stderr,
        )
        return 2

    expected = expected_cases(args.source_dir)
    if not expected:
        print(
            f"::error::no [{ATTRIBUTE}] cases found under {args.source_dir} — the "
            "real-GDAL coverage this job exists to run has been deleted or the "
            "attribute renamed; update this guard together with it",
            file=sys.stderr,
        )
        return 1

    outcomes = trx_outcomes(args.trx)
    missing = sorted(name for name in expected if name not in outcomes)
    skipped = sorted(
        name for name in expected
        if outcomes.get(name) in _NOT_RUN_OUTCOMES
    )
    executed = sorted(
        name for name in expected
        if name in outcomes and outcomes[name] not in _NOT_RUN_OUTCOMES
    )

    if args.summary_file is not None:
        lines = [
            "### Real-GDAL coverage",
            "",
            f"- `[{ATTRIBUTE}]` cases declared: **{len(expected)}**",
            f"- executed: **{len(executed)}**",
            f"- skipped (GDAL CLI unavailable): **{len(skipped)}**",
            f"- absent from the TRX: **{len(missing)}**",
            "",
        ]
        for name in sorted(expected):
            state = outcomes.get(name, "absent")
            lines.append(f"- `{name}` → `{state}`")
        lines.append("")
        with args.summary_file.open("a", encoding="utf-8") as handle:
            handle.write("\n".join(lines))

    for name in missing:
        print(f"::error::[{ATTRIBUTE}] case absent from the TRX: {name}", file=sys.stderr)
    for name in skipped:
        print(
            f"::error::[{ATTRIBUTE}] case did not run ({outcomes[name]}): {name} — "
            "the GDAL CLI is missing from this runner, so the real-GDAL coverage "
            "silently disappeared",
            file=sys.stderr,
        )
    if missing or skipped:
        return 1

    print(
        f"OK: all {len(executed)} [{ATTRIBUTE}] case(s) executed against a real "
        "GDAL CLI (0 skipped)."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
