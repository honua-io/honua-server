#!/usr/bin/env python3
"""Check optional PR admission boundaries and retained trunk link coverage."""
import fnmatch
from pathlib import Path
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[3]


def workflow(name):
    return yaml.load((ROOT / ".github/workflows" / name).read_text(), Loader=yaml.BaseLoader)


def admitted(name, path):
    patterns = workflow(name)["on"]["pull_request"]["paths"]
    # These filters use literals and terminal **; fnmatch has the same matching
    # behavior for those patterns, including nested paths.
    return any(fnmatch.fnmatchcase(path, pattern) for pattern in patterns)


class OptionalInputTests(unittest.TestCase):
    def test_normalization_retains_generators_receipts_and_build_inputs(self):
        name = "normalize-derived-artifacts.yml"
        for path in ("src/Honua.Server/Program.cs", "tests/dotnet/Honua.Server.Tests/NewTests.cs",
                     "tests/baselines/client-compat/maplibre/latest.cert.json",
                     "docs/gis/data/capability-route-mapping.v1.json", "global.json"):
            self.assertTrue(admitted(name, path), path)
        for path in ("docs/gis/new-guide.md", "tests/python/unrelated_test.py", "README.md"):
            self.assertFalse(admitted(name, path), path)

    def test_capability_impact_keeps_selector_and_runtime_inputs(self):
        name = "capability-impact-comparison.yml"
        for path in ("src/Honua.Server/Program.cs", ".github/ci-shards.json",
                     "scripts/ci/honua-server-targeted-tests.sh", "scripts/ci/capability-impact.py"):
            self.assertTrue(admitted(name, path), path)
        for path in ("README.md", ".github/workflows/unrelated.yml", "docs/guides/new-guide.md"):
            self.assertFalse(admitted(name, path), path)

    def test_runtime_links_still_gate_affected_and_full_trunk_runs(self):
        name = "docs-link-gate.yml"
        for path in ("src/Honua.Server/Program.cs", "docker/example.yml", "eng/example.json"):
            self.assertFalse(admitted(name, path), path)
        self.assertTrue(admitted(name, "docs/operations/runbook.md"))
        ci = workflow("ci.yml")
        changes = ci["jobs"]["changes"]
        self.assertEqual(changes["if"], "${{ github.event_name != 'merge_group' }}")
        scripts = "\n".join(step.get("run", "") for step in changes["steps"])
        self.assertIn("python3 scripts/ci/check-doc-links.test.py", scripts)
        self.assertIn("python3 scripts/ci/check-doc-links.py", scripts)
        self.assertIn("changes", ci["jobs"]["ci-gate"]["needs"])
        self.assertIn("workflow_dispatch", ci["on"])
        self.assertIn("schedule", ci["on"])

    def test_interop_dependency_resolution_changes_remain_admitted(self):
        for name in ("geoarrow-interop-fixture.yml", "geoparquet-interop.yml"):
            for path in ("global.json", "NuGet.config"):
                self.assertTrue(admitted(name, path), (name, path))


if __name__ == "__main__":
    unittest.main()
