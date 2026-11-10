#!/usr/bin/env python3
"""Verify external OGC suites run manually and on every release-candidate build."""
from pathlib import Path
import unittest
import yaml

ROOT = Path(__file__).resolve().parents[3]
WORKFLOWS = ROOT / ".github/workflows"


def workflow(name):
    return yaml.load((WORKFLOWS / name).read_text(), Loader=yaml.BaseLoader)


class OgcReleaseCandidateTests(unittest.TestCase):
    def test_conformance_dispatchers_have_no_pr_push_or_schedule_trigger(self):
        names = set(path.name for path in WORKFLOWS.glob("cite-*-conformance.yml"))
        names.update(("cite-conformance.yml", "cite-evidence-report.yml",
                      "ogc-maps-conformance.yml", "ogc-api-building-block-conformance.yml"))
        calls = workflow("ogc-rc-conformance.yml")["jobs"]
        targets = {job["uses"].split("/")[-1] for job in calls.values()}
        self.assertEqual(names, targets)
        for name in names:
            with self.subTest(workflow=name):
                self.assertEqual(set(workflow(name)["on"]), {"workflow_dispatch", "workflow_call"})

    def test_every_release_candidate_waits_for_conformance(self):
        jobs = workflow("release-bundle.yml")["jobs"]
        call = jobs["ogc-conformance"]
        self.assertEqual(call["needs"], "rc-image")
        self.assertEqual(call["if"], "${{ github.event.inputs.channel == 'rc' }}")
        self.assertNotIn("run_integration", call["if"])
        self.assertEqual(call["with"]["server_image"], "${{ needs.rc-image.outputs.image_ref }}")
        self.assertEqual(call["with"]["source_sha"], "${{ needs.rc-image.outputs.server_sha }}")
        self.assertIn("ogc-conformance", jobs["aggregate"]["needs"])
        self.assertIn("github.event.inputs.channel != 'rc' || needs.ogc-conformance.result == 'success'",
                      jobs["aggregate"]["if"])

    def test_reusable_calls_supply_required_inputs(self):
        for job in workflow("ogc-rc-conformance.yml")["jobs"].values():
            target = workflow(job["uses"].split("/")[-1])
            inputs = (target["on"]["workflow_call"] or {}).get("inputs", {})
            supplied = job.get("with", {})
            self.assertLessEqual(set(supplied), set(inputs))
            for key, definition in inputs.items():
                if definition.get("required") == "true" and "default" not in definition:
                    self.assertIn(key, supplied)

    def test_independent_classic_profiles_keep_distinct_artifacts(self):
        jobs = workflow("cite-classic-conformance.yml")["jobs"]
        for job in jobs.values():
            self.assertEqual(job["with"]["artifact-suffix"], "-classic")
        steps = workflow("cite-conformance-common.yml")["jobs"]["cite"]["steps"]
        names = [step.get("with", {}).get("name", "") for step in steps]
        self.assertTrue(any("inputs.artifact-suffix" in name for name in names))


if __name__ == "__main__":
    unittest.main()
