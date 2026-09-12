#!/usr/bin/env python3
"""Tests for the official-SDK preview-tier certification fragment builder.

The defect this guards against: the builder used to fabricate a single
``positive`` facet per cell, so once the positive probes covered every governed
capability the preview job reported green without ever exercising the
authorization, isolation, paging or schema facets the governed roster requires.
The facets must come from ``docs/gis/data/client-certification-roster.v1.json``
and an unexercised facet must keep both the cell and the report failing.
"""

from __future__ import annotations

import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("build-sdk-release-certification.py")
SPEC = importlib.util.spec_from_file_location("build_sdk_release_certification", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
REPOSITORY_ROOT = SCRIPT.parents[2]
ROSTER = REPOSITORY_ROOT / "docs/gis/data/client-certification-roster.v1.json"

CAPABILITY = "serve.ogc-api-features"
SDKS = {
    "js": {"package": "@honua/sdk", "version": "0.0.0", "registry": "https://registry.npmjs.org"},
    "python": {"package": "honua-sdk", "version": "0.0.0", "registry": "https://pypi.org/simple"},
}
MANIFEST = {
    "candidate": {
        "sourceSha": "0" * 40,
        "imageDigest": "sha256:" + "1" * 64,
        "fixtureRevision": "tests/seed/base-schema.sql@" + "0" * 40,
        "contractRevision": "2026.1",
    },
    "sdks": SDKS,
    "capabilities": [CAPABILITY],
}


def probe_row(operation: str, result: str = "pass", facet: str | None = None) -> dict:
    row = {
        "capability": CAPABILITY,
        "operation": operation,
        "result": result,
        "startedAt": "2026-09-01T00:00:00Z",
        "completedAt": "2026-09-01T00:00:01Z",
    }
    if facet is not None:
        row["facet"] = facet
    return row


def build(results: dict[str, list[dict]], roster: Path = ROSTER) -> tuple[dict, dict]:
    """Run the builder over a synthetic results directory and return fragment + report."""
    with tempfile.TemporaryDirectory() as work:
        work_dir = Path(work)
        manifest_path = work_dir / "manifest.json"
        manifest_path.write_text(json.dumps(MANIFEST), encoding="utf-8")
        results_dir = work_dir / "results"
        results_dir.mkdir()
        installed = {sdk: {"installed": True, "registry": SDKS[sdk]["registry"]} for sdk in SDKS}
        (results_dir / "install-results.json").write_text(json.dumps(installed), encoding="utf-8")
        for sdk, observations in results.items():
            (results_dir / f"{sdk}.json").write_text(
                json.dumps({"observations": observations}), encoding="utf-8"
            )
        output = work_dir / "fragment.json"
        argv = [
            "build-sdk-release-certification.py",
            "--manifest", str(manifest_path),
            "--results-dir", str(results_dir),
            "--output", str(output),
            "--producer-source-sha", "2" * 40,
            "--roster", str(roster),
        ]
        original = sys.argv
        sys.argv = argv
        try:
            MODULE.main()
        finally:
            sys.argv = original
        return (
            json.loads(output.read_text(encoding="utf-8")),
            json.loads((output.parent / "report.json").read_text(encoding="utf-8")),
        )


def all_facets_passing() -> dict[str, list[dict]]:
    facets = MODULE.required_scenario_facets(ROSTER)
    return {
        sdk: [probe_row(f"{sdk}.{facet}", facet=facet) for facet in facets]
        for sdk in SDKS
    }


class RequiredFacetsTests(unittest.TestCase):
    def test_facets_are_read_from_the_governed_roster(self) -> None:
        roster = json.loads(ROSTER.read_text(encoding="utf-8"))
        row = next(e for e in roster["entries"] if e["id"] == MODULE.ROSTER_ENTRY_ID)
        self.assertEqual(MODULE.required_scenario_facets(ROSTER), row["scenarioFacets"])
        # The governed row is more than the positive path; if it ever collapses
        # to one facet this guard has stopped guarding anything.
        self.assertGreater(len(row["scenarioFacets"]), 1)

    def test_missing_roster_row_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as work:
            path = Path(work) / "roster.json"
            path.write_text(json.dumps({"entries": [{"id": "other"}]}), encoding="utf-8")
            with self.assertRaises(ValueError):
                MODULE.required_scenario_facets(path)

    def test_row_without_facets_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as work:
            path = Path(work) / "roster.json"
            path.write_text(
                json.dumps({"entries": [{"id": MODULE.ROSTER_ENTRY_ID, "scenarioFacets": []}]}),
                encoding="utf-8",
            )
            with self.assertRaises(ValueError):
                MODULE.required_scenario_facets(path)


class FacetEnforcementTests(unittest.TestCase):
    def test_positive_only_probes_cannot_certify_a_cell(self) -> None:
        fragment, report = build({sdk: [probe_row(f"{sdk}.positive")] for sdk in SDKS})
        facets = MODULE.required_scenario_facets(ROSTER)
        for observation in fragment["observations"]:
            self.assertEqual(observation["scenario_facets"], facets)
            self.assertEqual(set(observation["facet_results"]), set(facets))
            self.assertEqual(observation["facet_results"]["positive"]["result"], "pass")
            for facet in facets:
                if facet == "positive":
                    continue
                self.assertEqual(observation["facet_results"][facet]["result"], "fail")
                self.assertIsNone(observation["facet_results"][facet]["evidence_digest"])
            # Every positive probe passed, yet the cell must not be certified.
            self.assertEqual(observation["result"], "fail")
            self.assertIn("never exercised", observation["gap"])
        self.assertFalse(fragment["facet_scope"]["complete"])
        self.assertEqual(fragment["facet_scope"]["observed"], ["positive"])
        self.assertEqual(
            fragment["facet_scope"]["missing"], [f for f in facets if f != "positive"]
        )
        self.assertFalse(report["passed"])
        self.assertEqual(report["facetScope"], fragment["facet_scope"])

    def test_full_facet_coverage_certifies_the_cell(self) -> None:
        fragment, report = build(all_facets_passing())
        for observation in fragment["observations"]:
            self.assertEqual(observation["result"], "pass")
            self.assertIsNone(observation["gap"])
            self.assertTrue(
                all(v["result"] == "pass" for v in observation["facet_results"].values())
            )
        self.assertTrue(fragment["facet_scope"]["complete"])
        self.assertEqual(fragment["facet_scope"]["missing"], [])
        self.assertTrue(report["passed"])

    def test_one_failing_facet_fails_the_cell_and_the_report(self) -> None:
        results = all_facets_passing()
        results["python"][-1]["result"] = "fail"
        failed_facet = results["python"][-1]["facet"]
        fragment, report = build(results)
        cells = {
            (o["client_id"], o["capability_key"]): o for o in fragment["observations"]
        }
        self.assertEqual(cells[("python", CAPABILITY)]["result"], "fail")
        self.assertEqual(
            cells[("python", CAPABILITY)]["facet_results"][failed_facet]["result"], "fail"
        )
        self.assertEqual(cells[("js", CAPABILITY)]["result"], "pass")
        # Every governed facet was still exercised somewhere, so the scope is
        # complete; the report fails on the cell result instead.
        self.assertTrue(fragment["facet_scope"]["complete"])
        self.assertFalse(report["passed"])

    def test_ungoverned_facet_fails_closed(self) -> None:
        results = all_facets_passing()
        results["js"].append(probe_row("js.invented", facet="invented-facet"))
        with self.assertRaises(ValueError) as caught:
            build(results)
        self.assertIn("invented-facet", str(caught.exception))

    def test_preview_tier_and_profile_are_emitted(self) -> None:
        fragment, report = build(all_facets_passing())
        self.assertEqual(fragment["tier"], "preview")
        self.assertEqual(fragment["profile"], "preview-http-baseseed")
        self.assertEqual(report["tier"], "preview")
        self.assertEqual(report["profile"], "preview-http-baseseed")
        for observation in fragment["observations"]:
            self.assertEqual(observation["tier"], "preview")
            self.assertEqual(observation["profile"], "preview-http-baseseed")
            self.assertEqual(observation["evidence_receipt"]["tier"], "preview")
            self.assertEqual(observation["evidence_receipt"]["profile"], "preview-http-baseseed")


class GateTests(unittest.TestCase):
    """The producer's own jq gates must reject a facet-incomplete fragment."""

    def test_runner_gate_checks_facet_scope(self) -> None:
        runner = SCRIPT.with_name("run-sdk-release-certification.sh").read_text(encoding="utf-8")
        self.assertIn(".facet_scope.complete == true", runner)

    def test_workflow_gate_checks_facet_scope(self) -> None:
        workflow = REPOSITORY_ROOT / ".github/workflows/sdk-server-compatibility.yml"
        content = workflow.read_text(encoding="utf-8")
        self.assertIn('.tier == "preview" and .profile == "preview-http-baseseed"', content)
        self.assertIn("Published SDKs x preview HTTP base-seed", content)
        self.assertIn(".facet_scope.complete == true", content)

    def test_jq_rejects_an_incomplete_facet_scope(self) -> None:
        if subprocess.run(["which", "jq"], capture_output=True).returncode != 0:
            self.skipTest("jq is not installed")
        fragment, _ = build({sdk: [probe_row(f"{sdk}.positive")] for sdk in SDKS})
        with tempfile.TemporaryDirectory() as work:
            path = Path(work) / "fragment.json"
            path.write_text(json.dumps(fragment), encoding="utf-8")
            probe = subprocess.run(
                ["jq", "-e", ".facet_scope.complete == true", str(path)], capture_output=True
            )
            self.assertNotEqual(probe.returncode, 0)


if __name__ == "__main__":
    unittest.main(verbosity=2)
