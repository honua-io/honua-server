#!/usr/bin/env python3
"""Check optional PR admission boundaries and retained trunk link coverage."""
from functools import lru_cache
import json
import re
from pathlib import Path
import subprocess
import tempfile
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[3]


@lru_cache(maxsize=None)
def workflow(name):
    return yaml.load((ROOT / ".github/workflows" / name).read_text(), Loader=yaml.BaseLoader)


INVENTORY = "generate-workflow-inventory.py"
CONCEPTS = "generate-capability-concepts.py"


def write_generator(work, name, body=None):
    (work / "scripts/ci").mkdir(parents=True, exist_ok=True)
    (work / "scripts/ci" / name).write_text(
        body or f"from pathlib import Path\nPath('{name}.ran').write_text('')\n")


def git(work, *arguments):
    subprocess.run(["git", "-c", "user.name=fixture", "-c", "user.email=fixture@example.invalid",
                    "-c", "commit.gpgsign=false", *arguments],
                   cwd=work, check=True, capture_output=True)


def checkout_with_fork_point(work, generators):
    """Create a head checkout whose origin/trunk fork point carries the named generators."""
    git(work, "init", "-q")
    for name in generators:
        write_generator(work, name)
    git(work, "add", "-A")
    git(work, "commit", "-q", "-m", "fork point")
    git(work, "update-ref", "refs/remotes/origin/trunk", "HEAD")


def regenerate(work):
    """Run the Docs Link Gate regeneration step exactly as the workflow declares it."""
    steps = workflow("docs-link-gate.yml")["jobs"]["check"]["steps"]
    script = next(step["run"] for step in steps
                  if step.get("name") == "Regenerate trunk-owned documentation for validation")
    return subprocess.run(["bash", "-e", "-c", script], cwd=work,
                          capture_output=True, text=True, check=False)


def admitted(name, path):
    patterns = workflow(name)["on"]["pull_request"]["paths"]
    return any(path_matches(path, pattern) for pattern in patterns)


def path_matches(path, pattern):
    """Match the literal/*/** subset used by these GitHub paths filters."""
    parts = []
    index = 0
    while index < len(pattern):
        if pattern[index:index + 3] == "**/":
            parts.append("(?:.*/)?")
            index += 3
        elif pattern[index:index + 2] == "**":
            parts.append(".*")
            index += 2
        elif pattern[index] == "*":
            parts.append("[^/]*")
            index += 1
        else:
            parts.append(re.escape(pattern[index]))
            index += 1
    return re.fullmatch("".join(parts), path) is not None


class OptionalInputTests(unittest.TestCase):
    def test_normalization_retains_generators_receipts_and_build_inputs(self):
        name = "normalize-derived-artifacts.yml"
        for path in ("src/Honua.Server/Program.cs", "tests/dotnet/Honua.Server.Tests/NewTests.cs",
                     "tests/baselines/client-compat/maplibre/latest.cert.json",
                     "docs/gis/data/capability-route-mapping.v1.json", "global.json",
                     "tests/fixtures/plugins/Honua.Sample.UtilityValidationPlugin/Plugin.cs",
                     "Directory.Build.props", "src/Nested/Build.props"):
            self.assertTrue(admitted(name, path), path)
        for path in ("docs/gis/new-guide.md", "tests/python/unrelated_test.py", "README.md"):
            self.assertFalse(admitted(name, path), path)

    def test_github_globs_respect_directory_boundaries(self):
        self.assertTrue(path_matches("Directory.Build.props", "**/*.props"))
        self.assertTrue(path_matches("src/Build.props", "**/*.props"))
        self.assertFalse(path_matches("src/Nested.sln", "*.sln"))

    def test_docs_regeneration_tolerates_heads_cut_before_the_inventory_generator(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            checkout_with_fork_point(work, [CONCEPTS])
            missing = regenerate(work)
            self.assertEqual(missing.returncode, 0, missing.stderr)
            self.assertIn("::notice::", missing.stdout)
            self.assertTrue((work / f"{CONCEPTS}.ran").exists())
            write_generator(work, INVENTORY)
            present = regenerate(work)
            self.assertEqual(present.returncode, 0, present.stderr)
            self.assertNotIn("::notice::", present.stdout)
            self.assertTrue((work / f"{INVENTORY}.ran").exists())
            write_generator(work, INVENTORY, "raise SystemExit(3)\n")
            self.assertNotEqual(regenerate(work).returncode, 0, "a present inventory generator that fails must fail")
            write_generator(work, INVENTORY)
            (work / "scripts/ci" / CONCEPTS).unlink()
            self.assertNotEqual(regenerate(work).returncode, 0, "the capability concepts generator stays mandatory")

    def test_docs_regeneration_rejects_a_head_that_removes_the_inventory_generator(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            checkout_with_fork_point(work, [CONCEPTS, INVENTORY])
            (work / "scripts/ci" / INVENTORY).unlink()
            removed = regenerate(work)
            self.assertNotEqual(removed.returncode, 0)
            self.assertIn("::error::", removed.stdout)
            self.assertFalse((work / f"{CONCEPTS}.ran").exists())

    def test_docs_regeneration_fails_closed_without_a_trunk_fork_point(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            checkout_with_fork_point(work, [CONCEPTS])
            git(work, "update-ref", "-d", "refs/remotes/origin/trunk")
            unknown = regenerate(work)
            self.assertNotEqual(unknown.returncode, 0)
            self.assertNotIn("::notice::", unknown.stdout)

    def test_runtime_links_still_gate_affected_and_full_trunk_runs(self):
        name = "docs-link-gate.yml"
        for path in ("src/Honua.Server/Program.cs", "docker/example.yml", "eng/example.json"):
            self.assertFalse(admitted(name, path), path)
        self.assertTrue(admitted(name, "docs/operations/runbook.md"))
        ci = workflow("ci.yml")
        router = ci["jobs"]["ci-router-validation"]
        steps = [step for step in router["steps"] if "check-doc-links.py" in step.get("run", "")]
        self.assertEqual(len(steps), 1)
        self.assertIn("python3 scripts/ci/check-doc-links.test.py", steps[0]["run"])
        self.assertNotIn("if", steps[0])
        self.assertNotIn("continue-on-error", steps[0])
        self.assertNotIn("continue-on-error", router)
        self.assertIn("ci-router-validation", ci["jobs"]["ci-gate"]["needs"])
        self.assertFalse(any("check-doc-links.py" in step.get("run", "")
                             for step in ci["jobs"]["changes"]["steps"]))
        self.assertIn("workflow_dispatch", ci["on"])
        self.assertIn("schedule", ci["on"])

    def test_interop_dependency_resolution_changes_remain_admitted(self):
        for name in ("geoarrow-interop-fixture.yml", "geoparquet-interop.yml"):
            for path in ("global.json", "NuGet.config", "Directory.Packages.props"):
                self.assertTrue(admitted(name, path), (name, path))

    def test_interop_pull_requests_admit_only_format_named_inputs(self):
        cases = {
            "geoparquet-interop.yml": (
                ("src/Honua.Hosting/Features/Services/GeoParquetFeatureWriter.cs",
                 "src/Honua.Protocols/OData/Features/ODataGeoParquetExport.cs",
                 "src/Honua.Geometry/Features/FileImport/Services/GeoParquetReader.cs",
                 "src/Honua.Core/Core/Features/Infrastructure/Crs/Resources/geoparquet-crs-projjson.json",
                 "tests/dotnet/Honua.TestKit.Core/GeoParquetTestFactory.cs",
                 "tests/dotnet/Honua.Server.Tests/TestData/GeoParquet/README.md",
                 "tests/python/feature_server/test_geoparquet_interop.py", "tests/seed/example.sql",
                 "src/Honua.Protocols/GeoServices/FeatureServer/Services/QueryFormatters.cs",
                 "src/Honua.Core/Core/Features/FileImport/Services/FileFormatDetectionService.cs",
                 "src/Honua.Db/Postgres/Features/FileImport/StreamingFileImportService.Readers.cs",
                 "src/Honua.Core/Core/Configuration/LimitsOptions.cs"),
                ("tests/dotnet/Honua.TestKit/Fixture.cs", "src/Honua.Protocols/OData/Features/Other.cs",
                 ".github/actions/setup-dotnet-ci/action.yml", "Directory.Build.props")),
            "geoarrow-interop-fixture.yml": (
                ("src/Honua.Protocols/GeoServices/FeatureServer/Services/GeoArrowQueryFormatter.cs",
                 "tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/FeatureServer/Services/GeoArrowQueryFormatterTests.cs"),
                ("src/Honua.Hosting/Features/Services/Other.cs", "src/Honua.Core/Abstractions/Example.cs",
                 ".github/actions/setup-dotnet-ci/action.yml", "Directory.Build.targets")),
        }
        for name, (kept, moved) in cases.items():
            for path in kept:
                self.assertTrue(admitted(name, path), (name, path))
            for path in moved:
                self.assertFalse(admitted(name, path), (name, path))

    def test_interop_inputs_outside_pull_requests_run_in_the_full_trunk_run(self):
        ci = workflow("ci.yml")
        full = "${{ (github.event_name == 'workflow_dispatch' && inputs.full_ci == true) || github.event_name == 'schedule' }}"
        for job, name in (("geoparquet-interop", "geoparquet-interop.yml"),
                          ("geoarrow-interop", "geoarrow-interop-fixture.yml")):
            self.assertIn("workflow_call", workflow(name)["on"])
            self.assertEqual(ci["jobs"][job]["uses"], f"./.github/workflows/{name}")
            self.assertEqual(ci["jobs"][job]["if"], full)
            self.assertIn(job, ci["jobs"]["ci-gate"]["needs"])
            # A called run must never share a cancel-in-progress group with a
            # standalone trunk dispatch of the same workflow.
            group = workflow(name)["concurrency"]["group"]
            self.assertTrue(group.endswith("${{ github.event.pull_request.number || github.run_id }}"), group)
        gate = ci["jobs"]["ci-gate"]["steps"][0]
        self.assertIn('"${GEOPARQUET_RESULT}" != success', gate["run"])
        self.assertIn('"${GEOARROW_RESULT}" != success', gate["run"])

    def test_docs_links_admit_pages_and_checker_data_only(self):
        name = "docs-link-gate.yml"
        for path in ("docs/index.md", "docs/gis/new-guide.md", "docs/internal/ci/gate-model.md",
                     "docs/gis/data/capability-matrix.v1.json", "docs/gis/data/capability-keys.v1.json",
                     "docs/get-started/install-artifacts.json", "docs/okf/domain-graph.v1.json",
                     "docs/llms.txt", "docs/.gitbook.yaml",
                     "docs/guides/deploy/examples/prometheus-alerts.yml"):
            self.assertTrue(admitted(name, path), path)
        for path in ("docs/gis/data/feature-catalog.json", "docs/gis/data/geoservices-rest-parity.json"):
            self.assertFalse(admitted(name, path), path)


if __name__ == "__main__":
    unittest.main()
