#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <trx-file> <gap-report-path>" >&2
  exit 2
fi

trx_file="$1"
report_path="$2"

if [[ ! -f "$trx_file" ]]; then
  echo "TRX file not found: $trx_file" >&2
  exit 1
fi

mkdir -p "$(dirname "$report_path")"

python3 - "$trx_file" "$report_path" <<'PY'
from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path


trx_path = Path(sys.argv[1])
report_path = Path(sys.argv[2])


@dataclass(frozen=True)
class Result:
    name: str
    outcome: str
    source: str
    protocol: str
    details: str


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def text_for_descendant(element: ET.Element, name: str) -> str:
    for descendant in element.iter():
        if local_name(descendant.tag) == name and descendant.text:
            return descendant.text.strip()
    return ""


def classify_source(test_name: str) -> str:
    lower = test_name.lower()
    if "geoserver" in lower:
        return "GeoServer"
    if "mapserver" in lower:
        return "MapServer"
    return "Unknown"


def classify_protocol(test_name: str) -> str:
    method = test_name.rsplit(".", 1)[-1].lower()
    if method.startswith("wms"):
        return "WMS 1.3"
    if method.startswith("wfs"):
        return "WFS 2.0"
    if method.startswith("wmts"):
        return "WMTS 1.0"
    return "Unknown"


def short_test_name(test_name: str) -> str:
    return test_name.rsplit(".", 1)[-1]


def escape_markdown(value: str) -> str:
    return value.replace("|", "\\|").replace("\n", " ").strip()


def parse_results(path: Path) -> list[Result]:
    tree = ET.parse(path)
    results: list[Result] = []

    for element in tree.iter():
        if local_name(element.tag) != "UnitTestResult":
            continue

        test_name = element.attrib.get("testName") or element.attrib.get("testId") or "unknown"
        outcome = element.attrib.get("outcome", "Unknown")
        message = text_for_descendant(element, "Message")
        stdout = text_for_descendant(element, "StdOut")
        combined = "\n".join(part for part in [message, stdout] if part)

        gap_match = re.search(r"gap:\s*([^\r\n<]+)", combined, flags=re.IGNORECASE)
        details = gap_match.group(0).strip() if gap_match else message.splitlines()[0].strip() if message else ""

        results.append(
            Result(
                name=short_test_name(test_name),
                outcome=outcome,
                source=classify_source(test_name),
                protocol=classify_protocol(test_name),
                details=details,
            )
        )

    return results


def write_section(lines: list[str], title: str, rows: list[Result], empty_text: str) -> None:
    lines.append(f"## {title}")
    lines.append("")
    if not rows:
        lines.append(empty_text)
        lines.append("")
        return

    lines.append("| Source | Protocol | Test | Details |")
    lines.append("|---|---|---|---|")
    for row in rows:
        lines.append(
            "| "
            + " | ".join(
                [
                    escape_markdown(row.source),
                    escape_markdown(row.protocol),
                    f"`{escape_markdown(row.name)}`",
                    escape_markdown(row.details or row.outcome),
                ]
            )
            + " |"
        )
    lines.append("")


def main() -> None:
    results = parse_results(trx_path)
    open_gaps = [
        result for result in results
        if result.outcome.lower() in {"notexecuted", "skipped"} and result.details.lower().startswith("gap:")
    ]
    failures = [
        result for result in results
        if result.outcome.lower() in {"failed", "error", "timeout"}
    ]
    passing = [
        result for result in results
        if result.outcome.lower() == "passed"
    ]

    generated_at = datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    lines: list[str] = [
        "# Cross-Server Consume Gap Report",
        "",
        f"Last refreshed: {generated_at}",
        "",
        "This report is generated from the nightly cross-server consume suite. It tracks Honua-as-client reads against reference GeoServer and MapServer sources for WMS 1.3, WFS 2.0, and WMTS 1.0.",
        "",
        f"Source TRX: `{trx_path.as_posix()}`",
        "",
        "| Outcome | Count |",
        "|---|---:|",
        f"| Passing | {len(passing)} |",
        f"| Open gaps | {len(open_gaps)} |",
        f"| Failures | {len(failures)} |",
        "",
    ]

    write_section(lines, "Open Gaps", open_gaps, "No open compatibility gaps were reported by skipped tests.")
    write_section(lines, "Failures", failures, "No failing consume tests were reported.")
    write_section(lines, "Passing", passing, "No passing consume tests were reported.")

    while lines and not lines[-1]:
        lines.pop()

    report_path.write_text("\n".join(lines) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
PY
