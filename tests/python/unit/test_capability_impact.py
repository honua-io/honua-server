import importlib.util
import json
import tempfile
import unittest
from contextlib import ExitStack
from pathlib import Path
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location("capability_impact", ROOT / "scripts/ci/capability-impact.py")
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader
SPEC.loader.exec_module(MODULE)


class CapabilityImpactTests(unittest.TestCase):
    def test_filter_parser_honors_grouping_and_exclusions(self):
        expression = "(FullyQualifiedName~Wfs20|FullyQualifiedName~Wps20)&FullyQualifiedName!~Endpoints"
        self.assertTrue(MODULE.FilterParser(expression, "Honua.Server.Tests.Wps20.Execute").evaluate())
        self.assertFalse(MODULE.FilterParser(expression, "Honua.Server.Tests.Wps20Endpoints.Execute").evaluate())
        self.assertFalse(MODULE.FilterParser(expression, "Honua.Server.Tests.Wms.Execute").evaluate())

    def test_completeness_rejects_unsharded_proving_test(self):
        catalog = {"entries": [{"method": "GET", "route": "/x", "family": "X", "capability": "serve.x", "proving_tests": ["Other.Tests.X"]}]}
        keys = {"capabilities": [{"key": "serve.x"}], "crosswalks": {"interop": []}}
        config = {"shards": [{"name": "Known", "filter": "FullyQualifiedName~Honua.Server.Tests"}]}
        errors = MODULE.validate_graph(catalog, keys, config, {"provingTests": [], "routeFamilies": []})
        self.assertTrue(any("outside every CI shard" in error for error in errors))

    def test_capability_selection_maps_code_location_to_tests_shards_and_interop(self):
        catalog = {"entries": [{"method": "GET", "route": "/x", "family": "X", "code_location": "src/X.cs", "capability": "serve.x", "proving_tests": ["Honua.Server.Tests.XTests.Works"]}]}
        keys = {"capabilities": [{"key": "serve.x"}], "crosswalks": {"interop": [{"clientLane": "js", "protocol": "x", "capability": "serve.x"}]}}
        shards = {"unmapped_source_run_all_prefixes": ["src/"], "shards": [{"name": "X", "filter": "FullyQualifiedName~XTests"}]}
        with self._fixture(catalog, keys, shards) as fixture:
            report = MODULE.build_report(["src/X.cs"], {"run_all": False, "shards": ["X"]}, None, ["cap/serve"])
            self.assertEqual(report["capabilitySelection"]["capabilities"], ["serve.x"])
            self.assertEqual(report["capabilitySelection"]["shards"], ["X"])
            self.assertEqual(report["capabilitySelection"]["interopLanes"], [{"clientLane": "js", "protocol": "x"}])
            self.assertEqual(report["capabilityLabels"], ["cap/serve"])

    def test_unmapped_source_escalates_graph_selector_to_run_all(self):
        catalog = {"entries": []}
        keys = {"capabilities": [], "crosswalks": {"interop": []}}
        shards = {"unmapped_source_run_all_prefixes": ["src/"], "shards": [{"name": "A", "filter": "FullyQualifiedName~A"}, {"name": "B", "filter": "FullyQualifiedName~B"}]}
        with self._fixture(catalog, keys, shards):
            report = MODULE.build_report(["src/NewArea/File.cs"], {"run_all": False, "shards": ["A"]}, None, [])
            self.assertTrue(report["capabilitySelection"]["runAll"])
            self.assertEqual(report["capabilitySelection"]["shards"], ["A", "B"])

    def test_zero_envelopes_outside_selected_protocol_pairs_are_not_regressions(self):
        catalog = {"entries": [{"method": "GET", "route": "/x", "family": "X", "code_location": "src/X.cs", "capability": "serve.x", "proving_tests": ["Tests.X"]}]}
        keys = {"capabilities": [{"key": "serve.x"}, {"key": "serve.y"}], "crosswalks": {"interop": [{"clientLane": "js", "protocol": "x", "capability": "serve.x"}, {"clientLane": "js", "protocol": "y", "capability": "serve.y"}]}}
        shards = {"unmapped_source_run_all_prefixes": ["src/"], "shards": [{"name": "X", "filter": "FullyQualifiedName~Tests.X"}]}
        with self._fixture(catalog, keys, shards) as fixture:
            report = MODULE.build_report(["src/X.cs"], {"shards": ["X"]}, fixture / "empty", [])
            self.assertEqual(report["capabilitySelection"]["interopLanes"], [{"clientLane": "js", "protocol": "x"}])
            self.assertEqual({row["protocol"] for row in report["freshness"] if row["stale"]}, {"x", "y"})

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
        stack.root = root
        return _FixtureContext(stack, root)


class _FixtureContext:
    def __init__(self, stack, root):
        self.stack = stack
        self.root = root

    def __enter__(self):
        self.stack.__enter__()
        return self.root

    def __exit__(self, exc_type, exc_value, traceback):
        return self.stack.__exit__(exc_type, exc_value, traceback)


if __name__ == "__main__":
    unittest.main()
