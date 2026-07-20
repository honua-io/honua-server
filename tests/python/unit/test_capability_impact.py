import importlib.util
import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location("capability_impact", ROOT / "scripts/ci/capability-impact.py")
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader
SPEC.loader.exec_module(MODULE)


def test_filter_parser_honors_grouping_and_exclusions():
    expression = "(FullyQualifiedName~Wfs20|FullyQualifiedName~Wps20)&FullyQualifiedName!~Endpoints"
    assert MODULE.FilterParser(expression, "Honua.Server.Tests.Wps20.Execute").evaluate()
    assert not MODULE.FilterParser(expression, "Honua.Server.Tests.Wps20Endpoints.Execute").evaluate()
    assert not MODULE.FilterParser(expression, "Honua.Server.Tests.Wms.Execute").evaluate()


def test_completeness_rejects_unsharded_proving_test():
    catalog = {"entries": [{"method": "GET", "route": "/x", "family": "X", "capability": "serve.x", "proving_tests": ["Other.Tests.X"]}]}
    keys = {"capabilities": [{"key": "serve.x"}], "crosswalks": {"interop": []}}
    config = {"shards": [{"name": "Known", "filter": "FullyQualifiedName~Honua.Server.Tests"}]}
    errors = MODULE.validate_graph(catalog, keys, config, {"provingTests": [], "routeFamilies": []})
    assert any("outside every CI shard" in error for error in errors)


def test_capability_selection_maps_code_location_to_tests_shards_and_interop(tmp_path, monkeypatch):
    catalog = {"entries": [{"method": "GET", "route": "/x", "family": "X", "code_location": "src/X.cs", "capability": "serve.x", "proving_tests": ["Honua.Server.Tests.XTests.Works"]}]}
    keys = {"capabilities": [{"key": "serve.x"}], "crosswalks": {"interop": [{"clientLane": "js", "protocol": "x", "capability": "serve.x"}]}}
    shards = {"unmapped_source_run_all_prefixes": ["src/"], "shards": [{"name": "X", "filter": "FullyQualifiedName~XTests"}]}
    for name, value in (("catalog.json", catalog), ("keys.json", keys), ("shards.json", shards)):
        (tmp_path / name).write_text(json.dumps(value))
    monkeypatch.setattr(MODULE, "CATALOG", tmp_path / "catalog.json")
    monkeypatch.setattr(MODULE, "KEYS", tmp_path / "keys.json")
    monkeypatch.setattr(MODULE, "SHARDS", tmp_path / "shards.json")
    report = MODULE.build_report(["src/X.cs"], {"run_all": False, "shards": ["X"]}, None, ["cap/serve"])
    assert report["capabilitySelection"]["capabilities"] == ["serve.x"]
    assert report["capabilitySelection"]["shards"] == ["X"]
    assert report["capabilitySelection"]["interopLanes"] == [{"clientLane": "js", "protocol": "x"}]
    assert report["capabilityLabels"] == ["cap/serve"]


def test_unmapped_source_escalates_graph_selector_to_run_all(tmp_path, monkeypatch):
    catalog = {"entries": []}
    keys = {"capabilities": [], "crosswalks": {"interop": []}}
    shards = {"unmapped_source_run_all_prefixes": ["src/"], "shards": [{"name": "A", "filter": "FullyQualifiedName~A"}, {"name": "B", "filter": "FullyQualifiedName~B"}]}
    for name, value in (("catalog.json", catalog), ("keys.json", keys), ("shards.json", shards)):
        (tmp_path / name).write_text(json.dumps(value))
    monkeypatch.setattr(MODULE, "CATALOG", tmp_path / "catalog.json")
    monkeypatch.setattr(MODULE, "KEYS", tmp_path / "keys.json")
    monkeypatch.setattr(MODULE, "SHARDS", tmp_path / "shards.json")
    report = MODULE.build_report(["src/NewArea/File.cs"], {"run_all": False, "shards": ["A"]}, None, [])
    assert report["capabilitySelection"]["runAll"]
    assert report["capabilitySelection"]["shards"] == ["A", "B"]


def test_zero_envelopes_outside_selected_protocol_pairs_are_not_regressions(tmp_path, monkeypatch):
    catalog = {"entries": [{"method": "GET", "route": "/x", "family": "X", "code_location": "src/X.cs", "capability": "serve.x", "proving_tests": ["Tests.X"]}]}
    keys = {"capabilities": [{"key": "serve.x"}, {"key": "serve.y"}], "crosswalks": {"interop": [{"clientLane": "js", "protocol": "x", "capability": "serve.x"}, {"clientLane": "js", "protocol": "y", "capability": "serve.y"}]}}
    shards = {"unmapped_source_run_all_prefixes": ["src/"], "shards": [{"name": "X", "filter": "FullyQualifiedName~Tests.X"}]}
    for name, value in (("catalog.json", catalog), ("keys.json", keys), ("shards.json", shards)):
        (tmp_path / name).write_text(json.dumps(value))
    monkeypatch.setattr(MODULE, "CATALOG", tmp_path / "catalog.json")
    monkeypatch.setattr(MODULE, "KEYS", tmp_path / "keys.json")
    monkeypatch.setattr(MODULE, "SHARDS", tmp_path / "shards.json")
    report = MODULE.build_report(["src/X.cs"], {"shards": ["X"]}, tmp_path / "empty", [])
    assert report["capabilitySelection"]["interopLanes"] == [{"clientLane": "js", "protocol": "x"}]
    assert {row["protocol"] for row in report["freshness"] if row["stale"]} == {"x", "y"}
