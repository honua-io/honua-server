import importlib.util
import json
import tempfile
import unittest
from contextlib import ExitStack
from datetime import datetime, timedelta, timezone
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

    def test_mixed_mapped_and_unmapped_source_escalates_to_run_all(self):
        catalog = {"entries": [{"method": "GET", "route": "/x", "family": "X", "code_location": "src/X.cs", "capability": "serve.x", "proving_tests": ["Tests.X"]}]}
        keys = {"capabilities": [{"key": "serve.x"}], "crosswalks": {"interop": []}}
        shards = {"unmapped_source_run_all_prefixes": ["src/"], "shards": [{"name": "A", "filter": "FullyQualifiedName~Tests.X"}, {"name": "B", "filter": "FullyQualifiedName~Tests.B"}]}
        with self._fixture(catalog, keys, shards):
            report = MODULE.build_report(["src/X.cs", "src/Unmapped.cs"], {"shards": ["A"]}, None, [])
            self.assertTrue(report["capabilitySelection"]["runAll"])
            self.assertEqual(report["capabilitySelection"]["unmatchedSourceFiles"], ["src/Unmapped.cs"])
            self.assertEqual(report["capabilitySelection"]["shards"], ["A", "B"])

    def test_zero_envelopes_outside_selected_protocol_pairs_are_not_regressions(self):
        catalog = {"entries": [{"method": "GET", "route": "/x", "family": "X", "code_location": "src/X.cs", "capability": "serve.x", "proving_tests": ["Tests.X"]}]}
        keys = {"capabilities": [{"key": "serve.x"}, {"key": "serve.y"}], "crosswalks": {"interop": [{"clientLane": "js", "protocol": "x", "capability": "serve.x"}, {"clientLane": "js", "protocol": "y", "capability": "serve.y"}]}}
        shards = {"unmapped_source_run_all_prefixes": ["src/"], "shards": [{"name": "X", "filter": "FullyQualifiedName~Tests.X"}]}
        with self._fixture(catalog, keys, shards) as fixture:
            report = MODULE.build_report(["src/X.cs"], {"shards": ["X"]}, fixture / "empty", [])
            self.assertEqual(report["capabilitySelection"]["interopLanes"], [{"clientLane": "js", "protocol": "x"}])
            self.assertEqual({row["protocol"] for row in report["freshness"] if row["stale"]}, {"x", "y"})

    def test_freshness_uses_run_date_and_fails_closed_for_stale_future_and_invalid(self):
        now = datetime(2026, 7, 20, tzinfo=timezone.utc)
        passing = {"summary": {"total": 1, "passed": 1, "failed": 0, "skipped": 0, "not_applicable": 0}, "results": [{"status": "pass"}]}
        fresh = MODULE.freshness_state({**passing, "run_date": (now - timedelta(days=2)).isoformat()}, now)
        stale = MODULE.freshness_state({**passing, "run_date": (now - timedelta(days=15)).isoformat()}, now)
        future = MODULE.freshness_state({**passing, "run_date": (now + timedelta(days=1)).isoformat()}, now)
        invalid = MODULE.freshness_state({**passing, "run_date": "not-a-date"}, now)
        missing = MODULE.freshness_state(passing, now)
        self.assertFalse(fresh["stale"])
        self.assertEqual(fresh["observationProvenance"], "envelope.run_date")
        self.assertTrue(stale["stale"])
        self.assertTrue(future["stale"])
        self.assertTrue(invalid["stale"])
        self.assertTrue(missing["stale"])
        self.assertIsNone(invalid["ageDays"])

    def test_green_requires_authoritative_all_pass_terminal_summary(self):
        def envelope(status, **summary):
            return {"summary": {"total": 1, "passed": 1, "failed": 0, "skipped": 0, "not_applicable": 0, "cant_tell": 0, "errors": 0, **summary}, "results": [{"status": status}]}
        self.assertTrue(MODULE.is_green(envelope("pass")))
        for status in ("pending", "skip", "skipped", "unknown", "CantTell", "not-applicable", "fail"):
            self.assertFalse(MODULE.is_green(envelope(status)), status)
        self.assertFalse(MODULE.is_green(envelope("pass", skipped=1)))
        self.assertFalse(MODULE.is_green({"results": [{"status": "pass"}]}))
        missing_counter = envelope("pass")
        del missing_counter["summary"]["not_applicable"]
        self.assertFalse(MODULE.is_green(missing_counter))
        with_pending_extension = envelope("pass")
        with_pending_extension["extensions"] = [{"status": "pending"}]
        self.assertFalse(MODULE.is_green(with_pending_extension))

    def test_green_accepts_fully_accounted_gdal_na_heavy_envelope(self):
        envelope = {
            "summary": {
                "total": 24,
                "passed": 4,
                "failed": 0,
                "skipped": 0,
                "not_applicable": 20,
            },
            "results": [
                *({"status": "pass"} for _ in range(4)),
                *({"status": "not-applicable"} for _ in range(20)),
            ],
            "extensions": [{"status": "not_applicable"}],
        }
        self.assertTrue(MODULE.is_green(envelope))
        envelope["results"][-1] = {"status": "pending"}
        self.assertFalse(MODULE.is_green(envelope))

    def test_green_rejects_empty_or_missing_results(self):
        summary = {"total": 1, "passed": 1, "failed": 0, "skipped": 0, "not_applicable": 0}
        self.assertFalse(MODULE.is_green({"summary": summary}))
        self.assertFalse(MODULE.is_green({"summary": summary, "results": []}))

    def test_green_rejects_negative_or_noninteger_counters(self):
        summary = {"total": 1, "passed": 1, "failed": 0, "skipped": 0, "not_applicable": 0}
        for counter in summary:
            malformed = {**summary, counter: -1}
            self.assertFalse(MODULE.is_green({"summary": malformed, "results": [{"status": "pass"}]}), counter)
        for value in ("1", 1.0, True):
            malformed = {**summary, "total": value}
            self.assertFalse(MODULE.is_green({"summary": malformed, "results": [{"status": "pass"}]}), repr(value))

    def test_green_accepts_absent_optional_counters_and_rejects_malformed_or_nonzero_values(self):
        summary = {"total": 1, "passed": 1, "failed": 0, "skipped": 0, "not_applicable": 0}
        results = [{"status": "pass"}]
        self.assertTrue(MODULE.is_green({"summary": summary, "results": results}))
        for counter in ("cantTell", "cant_tell", "errors", "error"):
            for value in (-1, "0", 0.0, False):
                malformed = {**summary, counter: value}
                self.assertFalse(MODULE.is_green({"summary": malformed, "results": results}), f"{counter}={value!r}")
            nonzero = {**summary, counter: 1}
            self.assertFalse(MODULE.is_green({"summary": nonzero, "results": results}), counter)

    def test_exception_issue_and_route_family_semantics_are_validated(self):
        catalog = {"entries": [{"method": "GET", "route": "/x", "family": "Explicit family", "capability": None, "proving_tests": []}]}
        keys = {"capabilities": [], "crosswalks": {"interop": []}}
        config = {"shards": []}
        valid = {"provingTests": [], "routeFamilies": [{"family": "Explicit family", "reason": "No route capability", "issue": "https://github.com/honua-io/honua-server/issues/123"}]}
        invalid = {"provingTests": [], "routeFamilies": [{"family": "GET /x", "reason": "wrong semantics", "issue": "ticket-123"}]}
        self.assertEqual(MODULE.validate_graph(catalog, keys, config, valid), [])
        errors = MODULE.validate_graph(catalog, keys, config, invalid)
        self.assertTrue(any("invalid family exception" in error for error in errors))
        self.assertTrue(any("missing or unknown capability" in error for error in errors))

    def test_filter_parser_rejects_unconsumed_or_unsupported_syntax(self):
        for expression in ("Name~Foo", "FullyQualifiedName>Foo", "FullyQualifiedName~Foo trailing"):
            with self.assertRaises(ValueError, msg=expression):
                MODULE.FilterParser(expression, "Foo").evaluate()

    def test_every_current_shard_filter_is_fully_consumed(self):
        config = json.loads(MODULE.SHARDS.read_text(encoding="utf-8"))
        for shard in config["shards"]:
            MODULE.FilterParser(shard["filter"], "Honua.Server.Tests.Probe").evaluate()

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
