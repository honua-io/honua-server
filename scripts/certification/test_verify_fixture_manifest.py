from __future__ import annotations

import copy
import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("verify-fixture-manifest.py")
SPEC = importlib.util.spec_from_file_location("verify_fixture_manifest", SCRIPT)
module = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(module)

REPOSITORY_ROOT = module.repository_root(SCRIPT.parent)
REAL_MANIFEST = json.loads(
    (REPOSITORY_ROOT / module.MANIFEST_RELATIVE_PATH).read_text(encoding="utf-8"))


def digest(text: str) -> str:
    return "sha256:" + hashlib.sha256(text.encode("utf-8")).hexdigest()


class DigestAlgorithmTests(unittest.TestCase):
    def test_file_digest_is_sha256_of_raw_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "seed.sql"
            path.write_text("SELECT 1;\n", encoding="utf-8")
            self.assertEqual(digest("SELECT 1;\n"), module.file_digest(path))

    def test_input_set_digest_matches_sha256sum_pipeline(self):
        entries = [("b/second.yaml", digest("second")), ("a/first.sql", digest("first"))]
        rendered = (
            f"{digest('first').split(':')[1]}  a/first.sql\n"
            f"{digest('second').split(':')[1]}  b/second.yaml\n"
        )
        self.assertEqual(digest(rendered), module.input_set_digest(entries))

    def test_input_set_digest_is_order_independent(self):
        entries = [("a.sql", digest("a")), ("b.sql", digest("b"))]
        self.assertEqual(
            module.input_set_digest(entries), module.input_set_digest(list(reversed(entries))))

    def test_input_set_digest_changes_when_a_member_changes(self):
        before = [("a.sql", digest("a")), ("b.sql", digest("b"))]
        after = [("a.sql", digest("a")), ("b.sql", digest("b-changed"))]
        self.assertNotEqual(module.input_set_digest(before), module.input_set_digest(after))

    def test_input_set_digest_changes_when_a_member_is_added(self):
        before = [("a.sql", digest("a"))]
        after = [("a.sql", digest("a")), ("b.sql", digest("b"))]
        self.assertNotEqual(module.input_set_digest(before), module.input_set_digest(after))

    def test_input_set_digest_rejects_a_non_digest(self):
        with self.assertRaises(ValueError):
            module.input_set_digest([("a.sql", "deadbeef")])

    def test_canonical_json_sorts_members_and_drops_whitespace(self):
        self.assertEqual(
            '{"a":"1","z":{"a":true,"b":["x","y"]}}',
            module.canonical_json({"z": {"b": ["x", "y"], "a": True}, "a": "1"}))

    def test_canonical_json_is_insensitive_to_member_order(self):
        left = module.canonical_json({"a": "1", "b": "2"})
        right = module.canonical_json({"b": "2", "a": "1"})
        self.assertEqual(left, right)

    def test_canonical_json_rejects_numbers(self):
        with self.assertRaises(ValueError):
            module.canonical_json({"count": 1})

    def test_canonical_json_allows_booleans(self):
        self.assertEqual('{"flag":false}', module.canonical_json({"flag": False}))


class PythonProjectionParsingTests(unittest.TestCase):
    SOURCE = '\n'.join([
        'SERVICE_ID = "test_service"',
        'TOTAL = 10',
        'ANCHOR_LON = -122.4900',
        'BBOX = (-122.4900, 37.7100, -122.3700, 37.7900)',
        'FIELDS = (',
        '    "name",  # first',
        '    "status",',
        ')',
        'ALIAS = SERVICE_ID',
        'PATH = Path(__file__).parent',
        'FILTER = "status =="  # deliberately malformed',
    ])

    def setUp(self):
        self.literals, self.aliases = module.parse_python_constants(self.SOURCE)

    def test_parses_scalars(self):
        self.assertEqual("test_service", self.literals["SERVICE_ID"])
        self.assertEqual(10, self.literals["TOTAL"])
        self.assertAlmostEqual(-122.49, self.literals["ANCHOR_LON"])

    def test_parses_single_line_tuple(self):
        self.assertEqual([-122.49, 37.71, -122.37, 37.79], self.literals["BBOX"])

    def test_parses_multiline_tuple_and_strips_comments(self):
        self.assertEqual(["name", "status"], self.literals["FIELDS"])

    def test_records_aliases_instead_of_literals(self):
        self.assertEqual("SERVICE_ID", self.aliases["ALIAS"])
        self.assertNotIn("ALIAS", self.literals)

    def test_ignores_non_literal_right_hand_sides(self):
        self.assertNotIn("PATH", self.literals)

    def test_keeps_trailing_comment_out_of_a_string_value(self):
        self.assertEqual("status ==", self.literals["FILTER"])

    def test_values_match_compares_floats_with_tolerance(self):
        self.assertTrue(module.values_match(-122.49, -122.4900))
        self.assertFalse(module.values_match(-122.49, -122.48))


class ManifestVerificationTests(unittest.TestCase):
    def setUp(self):
        self.manifest = copy.deepcopy(REAL_MANIFEST)

    def test_committed_manifest_verifies_clean(self):
        self.assertEqual([], module.verify(self.manifest, REPOSITORY_ROOT))

    def test_committed_manifest_passes_through_main(self):
        self.assertEqual(0, module.main(["--quiet"]))

    def test_file_digest_drift_is_reported(self):
        self.manifest["inputs"][0]["sha256"] = digest("not the real file")
        problems = module.verify_inputs(self.manifest, REPOSITORY_ROOT)
        self.assertTrue(any("digest drift" in problem for problem in problems), problems)

    def test_missing_input_file_is_reported(self):
        self.manifest["inputs"][0]["path"] = "tests/seed/does-not-exist.sql"
        problems = module.verify_inputs(self.manifest, REPOSITORY_ROOT)
        self.assertTrue(any("does not exist" in problem for problem in problems), problems)

    def test_fixture_revision_drift_is_reported(self):
        self.manifest["fixtureRevision"] = digest("wrong")
        problems = module.verify_revisions(self.manifest)
        self.assertTrue(any("fixtureRevision drift" in problem for problem in problems), problems)

    def test_auth_policy_revision_drift_is_reported(self):
        self.manifest["authPolicy"]["policyId"] = "tampered"
        problems = module.verify_revisions(self.manifest)
        self.assertTrue(any("authPolicyRevision drift" in problem for problem in problems), problems)

    def test_ungoverned_not_applicable_reason_is_reported(self):
        binding = self.manifest["laneBindings"][0]["protocols"][0]
        binding["notApplicableCases"]["because-we-said-so"] = ["CERT-CONN-01"]
        problems = module.verify_case_graph(self.manifest)
        self.assertTrue(any("ungoverned" in problem for problem in problems), problems)

    def test_case_that_is_both_applicable_and_not_applicable_is_reported(self):
        binding = self.manifest["laneBindings"][0]["protocols"][0]
        reason = next(iter(binding["notApplicableCases"]))
        binding["notApplicableCases"][reason].append(binding["applicableCases"][0])
        problems = module.verify_case_graph(self.manifest)
        self.assertTrue(any("both applicable and not-applicable" in problem for problem in problems),
                        problems)

    def test_case_with_no_binding_and_no_unbound_reason_is_reported(self):
        self.manifest["cases"].append(
            {"id": "CERT-NEW-01", "scenarioFacetId": "SF-CONN-REACH", "description": "orphan"})
        problems = module.verify_case_graph(self.manifest)
        self.assertTrue(any("no lane binding" in problem for problem in problems), problems)

    def test_active_lane_without_a_projection_is_reported(self):
        self.manifest["laneBindings"] = [
            lane for lane in self.manifest["laneBindings"] if lane["laneId"] != "cli"]
        problems = module.verify_lane_coverage(self.manifest, REPOSITORY_ROOT)
        self.assertTrue(any("cli" in problem for problem in problems), problems)

    def test_auth_profile_claiming_realization_without_a_fixture_is_reported(self):
        profile = self.manifest["authPolicy"]["profiles"][0]
        profile["realizedByFixture"] = []
        problems = module.verify_auth_profiles(self.manifest)
        self.assertTrue(any("with no fixture" in problem for problem in problems), problems)

    def test_auth_profile_gap_without_a_gaps_entry_is_reported(self):
        profile = next(entry for entry in self.manifest["authPolicy"]["profiles"]
                       if entry["status"] == "gap")
        profile["gapId"] = "not-a-real-gap"
        problems = module.verify_auth_profiles(self.manifest)
        self.assertTrue(any("no matching gaps" in problem for problem in problems), problems)

    def test_missing_required_auth_profile_is_reported(self):
        self.manifest["authPolicy"]["profiles"] = [
            entry for entry in self.manifest["authPolicy"]["profiles"]
            if entry["id"] != "cross-tenant-denial"]
        problems = module.verify_auth_profiles(self.manifest)
        self.assertTrue(any("cross-tenant-denial" in problem for problem in problems), problems)

    def test_gap_without_a_tracking_issue_is_reported(self):
        self.manifest["gaps"][0]["trackingIssue"] = "https://example.com/issues/1"
        problems = module.verify_gaps(self.manifest)
        self.assertTrue(any("tracking issue" in problem for problem in problems), problems)

    def test_gap_without_a_reason_is_reported(self):
        self.manifest["gaps"][0]["reason"] = ""
        problems = module.verify_gaps(self.manifest)
        self.assertTrue(any("no reason" in problem for problem in problems), problems)

    def test_python_projection_drift_is_reported(self):
        self.manifest["pythonProjection"]["symbols"]["TOTAL_FEATURES"] = 11
        problems = module.verify_python_projection(self.manifest, REPOSITORY_ROOT)
        self.assertTrue(any("TOTAL_FEATURES drift" in problem for problem in problems), problems)

    def test_python_projection_alias_drift_is_reported(self):
        self.manifest["pythonProjection"]["aliases"]["ADMIN_PASSWORD"] = "SERVICE_ID"
        problems = module.verify_python_projection(self.manifest, REPOSITORY_ROOT)
        self.assertTrue(any("must alias" in problem for problem in problems), problems)

    def test_main_exits_non_zero_on_a_tampered_manifest(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "manifest.json"
            self.manifest["fixtureRevision"] = digest("wrong")
            path.write_text(json.dumps(self.manifest), encoding="utf-8")
            self.assertEqual(
                1, module.main(["--root", str(REPOSITORY_ROOT), "--manifest", str(path)]))

    def test_main_exits_non_zero_on_unparseable_json(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "manifest.json"
            path.write_text("{ not json", encoding="utf-8")
            self.assertEqual(
                2, module.main(["--root", str(REPOSITORY_ROOT), "--manifest", str(path)]))


if __name__ == "__main__":
    unittest.main()
