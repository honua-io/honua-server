#!/usr/bin/env python3
"""Check optional PR admission boundaries and retained trunk link coverage."""
from functools import lru_cache
import json
import re
from pathlib import Path
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[3]


@lru_cache(maxsize=None)
def workflow(name):
    return yaml.load((ROOT / ".github/workflows" / name).read_text(), Loader=yaml.BaseLoader)


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

    def test_capability_impact_keeps_selector_and_runtime_inputs(self):
        name = "capability-impact-comparison.yml"
        for path in ("src/Honua.Server/Program.cs", ".github/ci-shards.json",
                     "scripts/ci/honua-server-targeted-tests.sh", "scripts/ci/capability-impact.py"):
            self.assertTrue(admitted(name, path), path)
        for path in ("README.md", ".github/workflows/unrelated.yml", "docs/guides/new-guide.md"):
            self.assertFalse(admitted(name, path), path)

    def test_capability_impact_admits_every_legacy_selector_input(self):
        config = json.loads((ROOT / ".github/ci-shards.json").read_text())
        prefixes = config["infrastructure_paths"] + config["unmapped_source_run_all_prefixes"]
        prefixes += [path for shard in config["shards"] for path in shard["paths"]]
        for prefix in prefixes:
            path = prefix + "example.cs" if prefix.endswith("/") else prefix
            self.assertTrue(admitted("capability-impact-comparison.yml", path), path)

    def test_github_globs_respect_directory_boundaries(self):
        self.assertTrue(path_matches("Directory.Build.props", "**/*.props"))
        self.assertTrue(path_matches("src/Build.props", "**/*.props"))
        self.assertFalse(path_matches("src/Nested.sln", "*.sln"))

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
