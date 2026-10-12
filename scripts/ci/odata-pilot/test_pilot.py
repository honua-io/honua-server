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


class AdmissionEvidenceTests(unittest.TestCase):
    def test_builder_interval_must_be_finite_ordered_and_contained(self):
        self.assertTrue(pilot.bounded_interval(11, 19, 10, 20))
        for start, end in ((19, 11), (9, 19), (11, 21), (float("nan"), 19),
                           (11, float("inf")), (True, 19), ("11", 19), (None, 19)):
            self.assertFalse(pilot.bounded_interval(start, end, 10, 20))

    def test_builder_numeric_bounds_reject_bool_and_nonfinite_values(self):
        for value in (True, False, float("nan"), float("inf"), "128", None):
            self.assertFalse(pilot.finite_between(value, 1, 512))
        self.assertTrue(pilot.finite_between(128, 1, 512))

    def test_measured_component_rejects_precreated_receipt_before_subprocess(self):
        from types import SimpleNamespace
        with tempfile.TemporaryDirectory() as directory:
            receipt = Path(directory) / "host.json"
            receipt.write_text(json.dumps({"bounded": True, "ready": True, "inputKey": "matching"}))
            args = SimpleNamespace(selection="component", host_receipt=receipt)
            with patch.object(pilot, "command") as command, patch.object(pilot, "stage") as stage:
                with self.assertRaisesRegex(ValueError, "managed host lifecycle controller"):
                    pilot.run(args)
                command.assert_not_called()
                stage.assert_not_called()

    def test_component_comparison_cannot_promote_precreated_receipts(self):
        receipts = ComparisonTests().receipts()
        for receipt in receipts:
            receipt["selection"] = "component"
            receipt["host"] = {"queueWaitSeconds": 0, "clockSkewSeconds": 0}
        result = pilot.compare(receipts, "component")
        self.assertFalse(result["passed"])
        self.assertTrue(any("lifecycle" in issue for issue in result["issues"]))

    def test_component_edit_requires_post_build_host_callback(self):
        with self.assertRaisesRegex(ValueError, "controller callback"):
            pilot.validate_scenario("component", "odata-only-edit")
        pilot.validate_scenario("unit", "odata-only-edit")
        pilot.validate_scenario("component", "unchanged-incremental")


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

    def test_evaluation_capability_preflight_reports_unsupported_query_before_work(self):
        helper = self.root / "build-reservation.py"
        helper.write_text("# installed helper")
        with patch.object(pilot.subprocess, "run", return_value=subprocess.CompletedProcess([], 1, "", "unsupported reservation option: -getItem")) as probe:
            with self.assertRaisesRegex(ValueError, "No restore or build started"):
                pilot.evaluation_preflight(self.root, "src/Selected/Selected.csproj", "Release", "full", helper=helper)
        argv = probe.call_args.args[0]
        self.assertEqual("key", argv[2])
        self.assertTrue(any(arg.startswith("-getItem:") for arg in argv))

    def test_image_runtime_properties_are_explicit_and_identity_bound(self):
        portable = pilot.props("Release", "full")
        self.assertFalse(any("RuntimeIdentifier" in arg for arg in portable))
        with patch.object(pilot, "command", self.fake_command):
            x64 = pilot.evaluated_inputs(self.root, "src/Selected/Selected.csproj", "Release", "full", self.output, runtime="linux-x64")
            arm = pilot.evaluated_inputs(self.root, "src/Selected/Selected.csproj", "Release", "full", self.output, runtime="linux-arm64")
        self.assertIn("-p:RuntimeIdentifier=linux-x64", x64["identity"]["buildProperties"])
        self.assertIn("-p:SelfContained=false", x64["identity"]["buildProperties"])
        self.assertNotEqual(x64["key"], arm["key"])
        with self.assertRaisesRegex(ValueError, "only to image"):
            pilot.plan(self.root, "candidate", "unit", "Release", "full", "", "linux-x64")

    def test_build_only_analyzer_reference_missing_from_restore_graph_fails_closed(self):
        analyzer = self.root / "src/Honua.Analyzers/Honua.Analyzers.csproj"
        analyzer.parent.mkdir(parents=True)
        analyzer.write_text("<Project />")
        analyzer.with_name("Analyzer.cs").write_text("class BuildOnlyAnalyzer {}")
        def omitted_analyzer(argv, root):
            output = self.fake_command(argv, root)
            if any(arg.startswith("-getItem:") for arg in argv):
                evaluated = json.loads(output)
                evaluated["Items"]["ProjectReference"] = [{"FullPath": str(analyzer), "ReferenceOutputAssembly": "false"}]
                return json.dumps(evaluated)
            return output
        with patch.object(pilot, "command", side_effect=omitted_analyzer):
            with self.assertRaisesRegex(ValueError, "omitted from restore graph.*analyzer closure"):
                pilot.evaluated_inputs(self.root, "src/Selected/Selected.csproj", "Release", "full", self.output)

    def test_pilot_metadata_controls_are_used_and_part_of_identity(self):
        from unittest.mock import patch
        with patch.object(pilot, "command", side_effect=self.fake_command):
            first = self.identity()
            for key in ("IncludeSourceRevisionInInformationalVersion", "EnableSourceControlManagerQueries", "EnableSourceLink"):
                self.assertIn(f"-p:{key}=false", first["identity"]["buildProperties"])
            with patch.dict(pilot.PILOT_METADATA_PROPERTIES, {"EnableSourceLink": "true"}):
                changed = self.identity()
        self.assertNotEqual(first["key"], changed["key"])

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

    def test_runtime_json_rejects_nested_and_flat_secret_key_fields(self):
        runtime = self.root / "secret-publish"
        runtime.mkdir()
        settings = runtime / "appsettings.json"
        for key in ("MasterKey", "AdminPassword", "SigningKey", "PrivateKey", "EncryptionKey",
                    "Security:ConnectionEncryption:MasterKey", "Security__Signing_Key"):
            settings.write_text(json.dumps({"Security": [{key: "sensitive-value"}]}))
            with self.subTest(key=key), self.assertRaises(ValueError):
                pilot.ensure_runtime_only(runtime)
        settings.write_text(json.dumps({"Security": {"MasterKey": "", "AdminPassword": None, "PublicKey": "public-material"}}))
        pilot.ensure_runtime_only(runtime)

    def test_managed_builder_requires_fresh_matching_bounds_and_reservation(self):
        config = self.root / "builder.json"
        receipt = self.root / "builder-receipt.json"
        config.write_text(json.dumps({"argv": ["managed-builder", "{context}", "{tag}", "{inputKey}", "{receipt}"]}))
        builder = {"schema": "honua.odata-pilot-managed-builder/v1", "inputKey": "key", "image": "tag",
            "platform": "linux/amd64", "sharedAdmission": True, "boundedBuildKit": True, "reservationId": "owned-job",
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
            builder["platform"] = "linux/arm64"
            with self.assertRaises(ValueError):
                pilot.managed_builder(config, receipt, self.root, "tag", "pin", "assembly", "key", self.root, self.output, 10)
            receipt.unlink()
            builder["platform"] = "linux/amd64"
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
