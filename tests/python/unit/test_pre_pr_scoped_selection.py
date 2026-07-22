"""Unit tests for the local capability-scoped selection primitive (honua-server#2951).

`scripts/ci/pre-pr-check.sh` SMART mode calls `capability-impact.py select-local`
to narrow per-shard test filters without duplicating the changed-file ->
capability -> proving-test -> shard crosswalk that `capability-impact.py`
already owns (used today, report-only, by the hosted comparison job). These
tests exercise `compute_capability_selection` (the extracted primitive shared
by `build_report` and `select-local`) and the `select-local` CLI surface
directly, using isolated fixture catalogs/configs so they do not depend on the
size or shape of the real feature catalog.
"""

import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from contextlib import ExitStack
from pathlib import Path
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "scripts/ci/capability-impact.py"
SPEC = importlib.util.spec_from_file_location("capability_impact_local", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader
SPEC.loader.exec_module(MODULE)


class ComputeCapabilitySelectionTests(unittest.TestCase):
    def test_test_file_change_narrows_to_owning_capability_and_shard(self):
        catalog = {
            "entries": [
                {
                    "method": "GET",
                    "route": "/x",
                    "family": "X",
                    "code_location": "src/Honua.Server/EndpointRegistry.cs",
                    "capability": "serve.x",
                    "proving_tests": ["Honua.Server.Tests.Features.X.XTests.Works"],
                }
            ]
        }
        config = {
            "unmapped_source_run_all_prefixes": ["src/"],
            "shards": [{"name": "X", "filter": "FullyQualifiedName~XTests"}],
        }
        selection = MODULE.compute_capability_selection(
            ["tests/dotnet/Honua.Server.Tests/Features/X/XTests.cs"], catalog, config
        )
        self.assertFalse(selection["runAll"])
        self.assertEqual(selection["reason"], "capability_match")
        self.assertEqual(selection["capabilities"], ["serve.x"])
        self.assertEqual(selection["provingTests"], ["Honua.Server.Tests.Features.X.XTests.Works"])
        self.assertEqual(selection["shards"], ["X"])
        self.assertEqual(selection["unmatchedSourceFiles"], [])
        self.assertEqual(
            selection["testsByShard"], {"X": ["Honua.Server.Tests.Features.X.XTests.Works"]}
        )

    def test_unmapped_handler_source_change_escalates_to_run_all_and_empties_tests_by_shard(self):
        # Reflects the real catalog's current fidelity: code_location almost
        # always resolves to the endpoint-registry file (evidence-array
        # ordering, not per-handler), so a genuine handler-source edit cannot
        # match any entry and must fail closed to run_all rather than silently
        # under-select. See the PR description for the tracked follow-up.
        catalog = {
            "entries": [
                {
                    "method": "GET",
                    "route": "/x",
                    "family": "X",
                    "code_location": "src/Honua.Server/EndpointRegistry.cs",
                    "capability": "serve.x",
                    "proving_tests": ["Honua.Server.Tests.Features.X.XTests.Works"],
                }
            ]
        }
        config = {
            "unmapped_source_run_all_prefixes": ["src/"],
            "shards": [
                {"name": "X", "filter": "FullyQualifiedName~XTests"},
                {"name": "Y", "filter": "FullyQualifiedName~YTests"},
            ],
        }
        selection = MODULE.compute_capability_selection(
            ["src/Honua.Server/Features/X/XEndpoints.cs"], catalog, config
        )
        self.assertTrue(selection["runAll"])
        self.assertEqual(selection["reason"], "unmapped_graph_source")
        self.assertEqual(selection["shards"], ["X", "Y"])
        self.assertEqual(selection["unmatchedSourceFiles"], ["src/Honua.Server/Features/X/XEndpoints.cs"])
        self.assertEqual(selection["testsByShard"], {})

    def test_doc_only_change_is_not_unmatched_source_and_selects_nothing(self):
        catalog = {"entries": []}
        config = {"unmapped_source_run_all_prefixes": ["src/"], "shards": []}
        selection = MODULE.compute_capability_selection(["docs/gis/foo.md"], catalog, config)
        self.assertFalse(selection["runAll"])
        self.assertEqual(selection["reason"], "no_capability_match")
        self.assertEqual(selection["unmatchedSourceFiles"], [])
        self.assertEqual(selection["shards"], [])

    def test_tests_by_shard_groups_a_proving_test_under_every_matching_shard(self):
        catalog = {
            "entries": [
                {
                    "method": "GET",
                    "route": "/x",
                    "family": "X",
                    "code_location": "irrelevant.cs",
                    "capability": "serve.x",
                    "proving_tests": ["Honua.Server.Tests.Shared.SharedTests.Works"],
                }
            ]
        }
        config = {
            "unmapped_source_run_all_prefixes": [],
            "shards": [
                {"name": "A", "filter": "FullyQualifiedName~SharedTests"},
                {"name": "B", "filter": "FullyQualifiedName~SharedTests"},
            ],
        }
        selection = MODULE.compute_capability_selection(
            ["tests/dotnet/Honua.Server.Tests/Shared/SharedTests.cs"], catalog, config
        )
        self.assertEqual(selection["shards"], ["A", "B"])
        for shard in ("A", "B"):
            self.assertEqual(
                selection["testsByShard"][shard], ["Honua.Server.Tests.Shared.SharedTests.Works"]
            )


class BuildReportReusesSharedSelectionTests(unittest.TestCase):
    """`build_report`'s capabilitySelection must stay byte-identical to what it
    produced before the extraction — this guards against the refactor silently
    changing the hosted shadow-comparison report's shape."""

    def test_build_report_capability_selection_matches_compute_capability_selection(self):
        catalog = {
            "entries": [
                {
                    "method": "GET",
                    "route": "/x",
                    "family": "X",
                    "code_location": "src/X.cs",
                    "capability": "serve.x",
                    "proving_tests": ["Honua.Server.Tests.XTests.Works"],
                }
            ]
        }
        keys = {"capabilities": [{"key": "serve.x"}], "crosswalks": {"interop": []}}
        shards = {"unmapped_source_run_all_prefixes": ["src/"], "shards": [{"name": "X", "filter": "FullyQualifiedName~XTests"}]}
        with self._fixture(catalog, keys, shards):
            direct = MODULE.compute_capability_selection(["src/X.cs"], catalog, shards)
            report = MODULE.build_report(["src/X.cs"], {"shards": []}, None, [])
            self.assertEqual(report["capabilitySelection"]["runAll"], direct["runAll"])
            self.assertEqual(report["capabilitySelection"]["reason"], direct["reason"])
            self.assertEqual(report["capabilitySelection"]["capabilities"], direct["capabilities"])
            self.assertEqual(report["capabilitySelection"]["shards"], direct["shards"])
            self.assertEqual(report["capabilitySelection"]["unmatchedSourceFiles"], direct["unmatchedSourceFiles"])

    def _fixture(self, catalog, keys, shards):
        temporary_directory = tempfile.TemporaryDirectory()
        root = Path(temporary_directory.name)
        for name, value in (("catalog.json", catalog), ("keys.json", keys), ("shards.json", shards)):
            (root / name).write_text(json.dumps(value), encoding="utf-8")
        stack = ExitStack()
        stack.enter_context(temporary_directory)
        stack.enter_context(patch.object(MODULE, "CATALOG", root / "catalog.json"))
        stack.enter_context(patch.object(MODULE, "KEYS", root / "keys.json"))
        stack.enter_context(patch.object(MODULE, "SHARDS", root / "shards.json"))
        return stack


class SelectLocalCliTests(unittest.TestCase):
    """Exercises the actual `select-local` subprocess entrypoint pre-pr-check.sh
    shells out to, against the real repo catalog/shards — this is the
    integration seam bash relies on, so it is worth covering end-to-end rather
    than only through the importlib-loaded module."""

    def test_select_local_cli_runs_against_real_catalog_and_emits_expected_shape(self):
        with tempfile.NamedTemporaryFile(mode="w", suffix=".txt", delete=False) as handle:
            handle.write("docs/gis/nonexistent-doc-only-change.md\n")
            changed_files_path = handle.name
        try:
            result = subprocess.run(
                [sys.executable, str(SCRIPT), "select-local", "--changed-files", changed_files_path],
                cwd=ROOT,
                capture_output=True,
                text=True,
                check=True,
            )
        finally:
            Path(changed_files_path).unlink(missing_ok=True)
        payload = json.loads(result.stdout)
        self.assertEqual(payload["schemaVersion"], 1)
        self.assertEqual(payload["changedFileCount"], 1)
        self.assertFalse(payload["runAll"])
        self.assertEqual(payload["unmatchedSourceFiles"], [])
        for key in ("capabilities", "provingTests", "shards", "testsByShard"):
            self.assertIn(key, payload)

    def test_select_local_requires_changed_files_argument(self):
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "select-local"],
            cwd=ROOT,
            capture_output=True,
            text=True,
        )
        self.assertNotEqual(result.returncode, 0)


if __name__ == "__main__":
    unittest.main()
