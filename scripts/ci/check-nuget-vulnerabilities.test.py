#!/usr/bin/env python3
"""Offline contract tests for check-nuget-vulnerabilities.py."""

from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parent / "check-nuget-vulnerabilities.py"
spec = importlib.util.spec_from_file_location("check_nuget_vulnerabilities", SCRIPT)
gate = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(gate)

PROPS = """<Project>
  <ItemGroup>
    <NuGetAuditSuppress Include="https://github.com/advisories/GHSA-aaaa-aaaa-aaaa" />
  </ItemGroup>
</Project>
"""


def package(pid: str, version: str, *vulns: tuple[str, str]) -> dict:
    return {"id": pid, "resolvedVersion": version,
            "vulnerabilities": [{"severity": s, "advisoryurl": u} for s, u in vulns]}


def report(*frameworks: dict, problems: list | None = None) -> dict:
    projects = [{"path": f"/src/P{i}/P{i}.csproj", "frameworks": [fw]} for i, fw in enumerate(frameworks)]
    projects.append({"path": "/src/Clean/Clean.csproj"})  # no frameworks key when nothing is vulnerable
    return {"version": 1, "parameters": "--vulnerable --include-transitive",
            "problems": problems or [], "projects": projects}


class GateTests(unittest.TestCase):
    def run_gate(self, data: dict | str) -> tuple[int, str, dict]:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "r.json").write_text(data if isinstance(data, str) else json.dumps(data))
            (root / "Directory.Build.props").write_text(PROPS)
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                code = gate.main([str(root / "r.json"), "--props", str(root / "Directory.Build.props"),
                                  "--summary-json", str(root / "s.json")])
            summary = json.loads((root / "s.json").read_text()) if (root / "s.json").exists() else {}
            return code, out.getvalue(), summary

    def test_clean_report_passes(self) -> None:
        code, _, summary = self.run_gate(report())
        self.assertEqual(code, 0)
        self.assertEqual(summary["counts"], {"Critical": 0, "High": 0, "Moderate": 0, "Low": 0})

    def test_moderate_is_reported_but_not_blocking(self) -> None:
        fw = {"framework": "net10.0", "transitivePackages": [
            package("Lib.A", "1.0.0", ("Moderate", "https://github.com/advisories/GHSA-mmmm-mmmm-mmmm"))]}
        code, out, summary = self.run_gate(report(fw))
        self.assertEqual(code, 0)
        self.assertEqual(summary["counts"]["Moderate"], 1)
        self.assertIn("::warning::Moderate", out)

    def test_high_and_critical_block(self) -> None:
        for severity in ("High", "Critical"):
            fw = {"framework": "net10.0", "topLevelPackages": [
                package("Lib.B", "2.0.0", (severity, "https://github.com/advisories/GHSA-hhhh-hhhh-hhhh"))]}
            code, out, summary = self.run_gate(report(fw))
            self.assertEqual(code, 1, severity)
            self.assertEqual(summary["blocking"], 1)
            self.assertIn(f"::error::{severity} advisory", out)

    def test_suppressed_advisory_does_not_block(self) -> None:
        fw = {"framework": "net10.0", "transitivePackages": [
            package("SQLite.Native", "2.1.11", ("High", "https://github.com/advisories/GHSA-AAAA-aaaa-aaaa"))]}
        code, _, summary = self.run_gate(report(fw))
        self.assertEqual(code, 0)
        self.assertEqual(summary["counts"]["High"], 0)
        self.assertTrue(summary["findings"][0]["suppressed"])

    def test_same_advisory_across_projects_counts_once(self) -> None:
        fw = {"framework": "net10.0", "transitivePackages": [
            package("Lib.C", "3.0.0", ("High", "https://github.com/advisories/GHSA-cccc-cccc-cccc"))]}
        code, _, summary = self.run_gate(report(fw, fw, fw))
        self.assertEqual(code, 1)
        self.assertEqual(summary["counts"]["High"], 1)
        self.assertEqual(len(summary["findings"][0]["projects"]), 3)

    def test_report_errors_and_garbage_fail_closed(self) -> None:
        code, _, _ = self.run_gate(report(problems=[{"project": "x", "level": "error", "text": "boom"}]))
        self.assertEqual(code, 2)
        code, _, _ = self.run_gate("The following sources were used: not json")
        self.assertEqual(code, 2)
        code, _, _ = self.run_gate({"version": 1})
        self.assertEqual(code, 2)

    def test_repository_props_suppressions_parse(self) -> None:
        props = SCRIPT.parents[2] / "Directory.Build.props"
        self.assertIn("https://github.com/advisories/ghsa-2m69-gcr7-jv3q", gate.suppressed_advisories(props))


if __name__ == "__main__":
    unittest.main()
