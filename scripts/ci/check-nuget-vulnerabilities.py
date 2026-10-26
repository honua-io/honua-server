#!/usr/bin/env python3
"""Gate on `dotnet list package --vulnerable --format json` output.

High and Critical advisories fail the gate; Moderate and Low are reported but do
not block. Advisories risk-accepted with <NuGetAuditSuppress> in
Directory.Build.props (no upstream fix) are reported as suppressed, so that file
stays the single allowlist for restore-time audit and for this gate.

Usage:
  check-nuget-vulnerabilities.py <report.json> [--props Directory.Build.props]
      [--summary-json out.json]
Exit codes: 0 pass, 1 blocking advisories, 2 unusable report.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

BLOCKING = {"high", "critical"}
SEVERITIES = ("Critical", "High", "Moderate", "Low")


def suppressed_advisories(props: Path) -> set[str]:
    if not props.is_file():
        return set()
    root = ET.parse(props).getroot()
    found = set()
    for element in root.iter():
        if element.tag.rsplit("}", 1)[-1] == "NuGetAuditSuppress":
            url = (element.get("Include") or "").strip()
            if url:
                found.add(url.rstrip("/").lower())
    return found


def collect(report: dict) -> tuple[list[dict], list[str]]:
    """Return one finding per (package, version, advisory) plus report errors."""
    errors = [
        f"{p.get('project', '?')}: {p.get('text', '')}"
        for p in report.get("problems", []) or []
        if str(p.get("level", "")).lower() == "error"
    ]
    findings: dict[tuple[str, str, str], dict] = {}
    for project in report.get("projects", []) or []:
        for framework in project.get("frameworks", []) or []:
            for kind in ("topLevelPackages", "transitivePackages"):
                for package in framework.get(kind, []) or []:
                    for vuln in package.get("vulnerabilities", []) or []:
                        url = str(vuln.get("advisoryurl", "")).strip()
                        key = (package["id"], package.get("resolvedVersion", ""), url)
                        entry = findings.setdefault(key, {
                            "package": package["id"],
                            "version": package.get("resolvedVersion", ""),
                            "severity": str(vuln.get("severity", "Unknown")),
                            "advisory": url,
                            "projects": set(),
                        })
                        entry["projects"].add(Path(project.get("path", "?")).name)
    return list(findings.values()), errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("report", type=Path)
    parser.add_argument("--props", type=Path, default=Path("Directory.Build.props"))
    parser.add_argument("--summary-json", type=Path)
    args = parser.parse_args(argv)

    try:
        report = json.loads(args.report.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as exc:
        print(f"::error::Cannot read vulnerability report {args.report}: {exc}")
        return 2
    if not isinstance(report, dict) or "projects" not in report:
        print("::error::Vulnerability report has no 'projects' array; is it `--format json` output?")
        return 2

    findings, errors = collect(report)
    suppressed = suppressed_advisories(args.props)
    for f in findings:
        f["suppressed"] = f["advisory"].rstrip("/").lower() in suppressed
        f["blocking"] = f["severity"].lower() in BLOCKING and not f["suppressed"]
    findings.sort(key=lambda f: (not f["blocking"], f["severity"], f["package"], f["advisory"]))

    counts = {s: 0 for s in SEVERITIES}
    for f in findings:
        if not f["suppressed"]:
            counts[f["severity"].capitalize()] = counts.get(f["severity"].capitalize(), 0) + 1
    blocking = [f for f in findings if f["blocking"]]

    lines = ["## NuGet vulnerable packages", "",
             "| Severity | Count |", "| --- | --- |"]
    lines += [f"| {s} | {c} |" for s, c in counts.items()]
    lines.append(f"| Suppressed (NuGetAuditSuppress) | {sum(f['suppressed'] for f in findings)} |")
    if findings:
        lines += ["", "| Package | Version | Severity | Advisory | Status | Projects |",
                  "| --- | --- | --- | --- | --- | --- |"]
        for f in findings:
            status = "suppressed" if f["suppressed"] else ("BLOCKING" if f["blocking"] else "reported")
            lines.append(f"| {f['package']} | {f['version']} | {f['severity']} | {f['advisory']} | "
                         f"{status} | {len(f['projects'])} |")
    print("\n".join(lines))
    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_path:
        with open(summary_path, "a", encoding="utf-8") as handle:
            handle.write("\n".join(lines) + "\n")

    if args.summary_json:
        args.summary_json.write_text(json.dumps({
            "counts": counts,
            "blocking": len(blocking),
            "report_errors": errors,
            "findings": [{**f, "projects": sorted(f["projects"])} for f in findings],
        }, indent=2) + "\n", encoding="utf-8")

    for f in findings:
        if not f["blocking"] and not f["suppressed"]:
            print(f"::warning::{f['severity']} advisory {f['advisory']} in {f['package']} {f['version']}")
    for error in errors:
        print(f"::error::dotnet list package reported: {error}")
    for f in blocking:
        print(f"::error::{f['severity']} advisory {f['advisory']} in {f['package']} {f['version']} "
              f"({len(f['projects'])} project(s))")
    if errors:
        return 2
    return 1 if blocking else 0


if __name__ == "__main__":
    sys.exit(main())
