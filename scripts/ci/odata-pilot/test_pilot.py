#!/usr/bin/env python3
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import os
import subprocess
import sys
import time
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("odata_pilot", Path(__file__).with_name("pilot.py"))
pilot = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pilot)


class ComparisonTests(unittest.TestCase):
    def receipts(self):
        receipts = []
        for scenario in pilot.SCENARIOS:
            for pair in (1, 2, 3):
                for variant in ("baseline", "candidate"):
                    baseline = variant == "baseline"
                    receipts.append({"schema": pilot.SCHEMA, "selection": "unit", "scenario": scenario,
                        "pair": pair, "variant": variant, "qualified": True,
                        "artifact": {"projectCount": 20 if baseline else 8,
                            "identity": {"sdk": "10.0.100", "os": "Linux", "osVersion": "test", "architecture": "x86_64", "configuration": "Release", "composition": "full"}},
                        "stages": [{"name": "build", "queueWaitSeconds": 0, "clockSkewSeconds": 0}, {"name": "test", "queueWaitSeconds": 0, "clockSkewSeconds": 0}],
                        "firstTestSeconds": 100 if baseline else 60,
                        "peakProcessTreeRssBytes": 1000 if baseline else 700,
                        "tests": [{"identity": "Namespace.Class.Method|Method(arg:1)", "outcome": "Passed"}]})
        return receipts

    def test_nonfinite_metrics_and_clocks_cannot_qualify(self):
        for value in (float("nan"), float("inf"), float("-inf")):
            receipts = self.receipts()
            receipts[0]["stages"][0]["clockSkewSeconds"] = value
            self.assertFalse(pilot.compare(receipts, "unit")["passed"])
            receipts = self.receipts()
            receipts[0]["firstTestSeconds"] = value
            self.assertFalse(pilot.compare(receipts, "unit")["passed"])

    def test_all_three_pairs_and_all_thresholds_are_required(self):
        result = pilot.compare(self.receipts(), "unit")
        self.assertTrue(result["passed"])
        self.assertTrue(all(result["thresholds"].values()))
        self.assertFalse(pilot.compare(self.receipts()[:-1], "unit")["passed"])

    def test_fewer_or_different_tests_never_count_as_a_speedup(self):
        receipts = self.receipts()
        receipts[1]["tests"][0]["identity"] = "Namespace.Other.Method|Method(arg:1)"
        result = pilot.compare(receipts, "unit")
        self.assertFalse(result["passed"])
        self.assertTrue(any("test identities" in issue for issue in result["issues"]))

    def test_missing_memory_and_platform_mismatch_fail_closed(self):
        for mutate in (lambda r: r.update(peakProcessTreeRssBytes=None), lambda r: r["artifact"]["identity"].update(sdk="other")):
            receipts = self.receipts()
            mutate(receipts[1])
            self.assertFalse(pilot.compare(receipts, "unit")["passed"])

    def test_one_incremental_regression_is_not_hidden_by_median(self):
        receipts = self.receipts()
        next(r for r in receipts if r["scenario"] == "unchanged-incremental" and r["pair"] == 2 and r["variant"] == "candidate")["firstTestSeconds"] = 101
        result = pilot.compare(receipts, "unit")
        self.assertFalse(result["passed"])
        self.assertFalse(result["thresholds"]["noIncrementalRegression"])

    def test_unknown_queue_delays_and_wallclock_skew_prevent_promotion(self):
        for mutate in (lambda s: s.update(queueWaitSeconds=None), lambda s: s.update(queueWaitSeconds=5), lambda s: s.update(clockSkewSeconds=.2)):
            receipts = self.receipts()
            mutate(receipts[1]["stages"][0])
            self.assertFalse(pilot.compare(receipts, "unit")["passed"])

    def test_duplicates_fail_instead_of_selecting_best_run(self):
        receipts = self.receipts()
        receipts.append(copy.deepcopy(receipts[1]))
        self.assertFalse(pilot.compare(receipts, "unit")["passed"])

    def test_failed_or_skipped_receipt_is_not_qualified(self):
        receipts = self.receipts()
        receipts[1]["qualified"] = False
        self.assertFalse(pilot.compare(receipts, "unit")["passed"])


class InputTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.project = self.root / "src/Selected/Selected.csproj"
        self.project.parent.mkdir(parents=True)
        self.project.write_text("<Project />")
        self.source = self.project.with_name("Source.cs")
        self.source.write_text("class Source {}")
        self.linked = self.root / "fixtures/seed.sql"
        self.linked.parent.mkdir()
        self.linked.write_text("SELECT 1;")
        self.unrelated = self.root / "src/Unrelated/Other.cs"
        self.unrelated.parent.mkdir()
        self.unrelated.write_text("class Other {}")
        self.output = self.root / "evidence"
        self.output.mkdir()
        (self.root / "Directory.Build.props").write_text("<Project />")

    def tearDown(self):
        self.temporary.cleanup()

    def fake_command(self, argv, root):
        if "-t:GenerateRestoreGraphFile" in argv:
            output = Path(next(a.split("=", 1)[1] for a in argv if a.startswith("-p:RestoreGraphOutputPath=")))
            output.write_text(json.dumps({"projects": {str(self.project): {"frameworks": {"net10.0": {}}}}}))
            return ""
        if "--version" in argv:
            return "10.0.100"
        return json.dumps({"Properties": {"TargetFramework": "net10.0", "MSBuildAllProjects": str(self.root / "Directory.Build.props")},
            "Items": {"Compile": [{"FullPath": str(self.source)}], "EmbeddedResource": [{"FullPath": str(self.linked)}]}})

    def identity(self):
        with patch.object(pilot, "command", self.fake_command):
            return pilot.evaluated_inputs(self.root, "src/Selected/Selected.csproj", "Release", "full", self.output)

    def test_linked_assets_and_shared_props_invalidate_but_unrelated_protocol_does_not(self):
        initial = self.identity()
        self.unrelated.write_text("class Other { int unrelatedChange; }")
        self.assertEqual(initial["key"], self.identity()["key"])
        self.linked.write_text("SELECT 2;")
        changed_asset = self.identity()
        self.assertNotEqual(initial["key"], changed_asset["key"])
        (self.root / "Directory.Build.props").write_text("<Project><PropertyGroup /></Project>")
        self.assertNotEqual(changed_asset["key"], self.identity()["key"])
        self.assertEqual(1, initial["projectCount"])
        self.assertEqual(1, initial["compileItemCount"])

    def test_configuration_and_composition_are_artifact_identity(self):
        initial = self.identity()["key"]
        with patch.object(pilot, "command", self.fake_command):
            changed = pilot.evaluated_inputs(self.root, "src/Selected/Selected.csproj", "Debug", "slim", self.output)
        self.assertNotEqual(initial, changed["key"])

    def test_source_edit_restores_original_even_on_failed_command(self):
        source = self.root / "src/Honua.Protocols.OData/Example.cs"
        source.parent.mkdir()
        source.write_bytes(b"original\r\n")
        with self.assertRaisesRegex(ValueError, "stage failed"):
            with pilot.edit_source(self.root, "odata-only-edit", "src/Honua.Protocols.OData/Example.cs", 1):
                self.assertIn(b"paired edit 1", source.read_bytes())
                raise ValueError("stage failed")
        self.assertEqual(b"original\r\n", source.read_bytes())
        with self.assertRaises(ValueError):
            with pilot.edit_source(self.root, "odata-only-edit", "src/Other/Example.cs", 1):
                pass

    def test_runtime_context_rejects_compiler_and_credentials(self):
        runtime = self.root / "publish"
        runtime.mkdir()
        (runtime / "Honua.OData.TestHost.dll").write_bytes(b"app")
        pilot.ensure_runtime_only(runtime)
        for name in ("Microsoft.CodeAnalysis.CSharp.dll", "NuGet.Config", "certificate.pfx"):
            path = runtime / name
            path.write_bytes(b"not allowed")
            with self.assertRaises(ValueError):
                pilot.ensure_runtime_only(runtime)
            path.unlink()

    def test_managed_builder_requires_fresh_matching_bounds_and_reservation(self):
        config = self.root / "builder.json"
        receipt = self.root / "builder-receipt.json"
        config.write_text(json.dumps({"argv": ["managed-builder", "{context}", "{tag}", "{inputKey}", "{receipt}"]}))
        builder = {"schema": "honua.odata-pilot-managed-builder/v1", "inputKey": "key", "image": "tag",
            "sharedAdmission": True, "boundedBuildKit": True, "reservationId": "owned-job",
            "exitCode": 0, "peakJobMemoryBytes": 1000, "startedEpochSeconds": 2, "finishedEpochSeconds": 3,
            "limits": {"memoryMaxBytes": 1024**3, "cpuQuotaPercent": 200, "pidsMax": 128}}
        def fake_stage(name, argv, root, evidence, timeout):
            self.assertEqual(["managed-builder", str(self.root), "tag", "key", str(receipt)], argv)
            (evidence / "image").mkdir(exist_ok=True)
            pilot.write(receipt, builder)
            return {"startedEpochSeconds": 1, "finishedEpochSeconds": 4}
        with patch.object(pilot, "stage", fake_stage):
            pilot.managed_builder(config, receipt, self.root, "tag", "pin", "assembly", "key", self.root, self.output, 10)
            with self.assertRaises(ValueError):
                pilot.managed_builder(config, receipt, self.root, "tag", "pin", "assembly", "key", self.root, self.output, 10)
            receipt.unlink()
            builder["sharedAdmission"] = False
            with self.assertRaises(ValueError):
                pilot.managed_builder(config, receipt, self.root, "tag", "pin", "assembly", "key", self.root, self.output, 10)

    def test_trx_uses_case_identity_not_unstable_execution_guid(self):
        trx = self.root / "trx"
        trx.mkdir()
        (trx / "tests.trx").write_text('''<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <TestDefinitions><UnitTest id="random-guid"><TestMethod className="Ns.C" name="Case" /></UnitTest></TestDefinitions>
          <Results><UnitTestResult testId="random-guid" testName="Case(x: 1)" outcome="Passed" startTime="2026-01-01T00:00:00Z" /></Results></TestRun>''')
        self.assertEqual("Ns.C.Case|Case(x: 1)", pilot.trx_results(trx)[0]["identity"])
        (trx / "duplicate.trx").write_text((trx / "tests.trx").read_text())
        with self.assertRaises(ValueError):
            pilot.trx_results(trx)


class ContainmentTests(unittest.TestCase):
    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux subreaper containment")
    def test_timeout_stops_escaped_descendant_before_returning(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            evidence = root / "evidence"
            evidence.mkdir()
            child_pid = root / "child.pid"
            script = "import subprocess,sys,time; subprocess.Popen([sys.executable,'-c', 'import os,sys,time; os.setsid(); open(sys.argv[1], \"w\").write(str(os.getpid())); time.sleep(30)',sys.argv[1]]); time.sleep(30)"
            monotonic = time.monotonic
            def timeout_after_child_is_ready():
                return monotonic() + (100 if child_pid.exists() else 0)
            with patch.object(pilot.time, "monotonic", timeout_after_child_is_ready):
                with self.assertRaisesRegex(ValueError, "exceeded"):
                    pilot.stage("owned", [sys.executable, "-c", script, str(child_pid)], root, evidence, 20)
            pid = int(child_pid.read_text())
            self.assertFalse(Path(f"/proc/{pid}").exists())
            self.assertIsNotNone(json.loads((evidence / "owned/stage.json").read_text())["error"])

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux subreaper containment")
    def test_existing_child_is_not_assumed_to_belong_to_stage(self):
        foreign = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
        try:
            with tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                with self.assertRaises(pilot.ContainmentError):
                    pilot.stage("refused", [sys.executable, "-c", "pass"], root, root, 1)
            self.assertIsNone(foreign.poll())
        finally:
            foreign.terminate()
            foreign.wait()


if __name__ == "__main__":
    unittest.main()
