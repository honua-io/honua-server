"""honua-release nightly dispatch contract for the CNG producer.

The nightly orchestrator dispatches `cng-conformance.yml` with its catalog inputs
plus `nightly_dispatch_id`; an undeclared input is an HTTP 422 and no CNG evidence
is produced at all. honua-evidence then authenticates the run by its display title
`<name> [<dispatch_id>]` and by the five-field `producer_run` in each receipt.
"""
from __future__ import annotations

import importlib.util
import json
import sys
import tempfile
import unittest
import unittest.mock
from pathlib import Path

HERE = Path(__file__).parent
WORKFLOW = (HERE.parents[2] / ".github" / "workflows" / "cng-conformance.yml").read_text(encoding="utf-8")
TITLE = "Cloud-Native-Geospatial Conformance"
DISPATCH = "nightly-certification-40367649673-1"


def _load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


try:
    PRODUCER_RUN = _load("producer_run", HERE / "producer_run.py")
except FileNotFoundError:  # trunk before the contract: tests below fail, not error.
    PRODUCER_RUN = None
VALIDATOR = _load("validate_canonical_artifacts_dispatch", HERE / "validate-canonical-artifacts.py")


def dispatch_env(**overrides: str) -> dict[str, str]:
    env = {
        "GITHUB_EVENT_NAME": "workflow_dispatch",
        "GITHUB_REPOSITORY": "honua-io/honua-server",
        "GITHUB_WORKFLOW_REF": (
            "honua-io/honua-server/.github/workflows/cng-conformance.yml"
            "@refs/tags/nightly-candidate/" + "a" * 40),
        "GITHUB_RUN_ID": "987654321",
        "GITHUB_RUN_ATTEMPT": "2",
    }
    env.update(overrides)
    return env


EXPECTED_RUN = {
    "repository": "honua-io/honua-server",
    "workflow": "cng-conformance.yml",
    "run_id": 987654321,
    "run_attempt": 2,
    "dispatch_id": DISPATCH,
}


class WorkflowContractTests(unittest.TestCase):
    def test_dispatch_declares_the_optional_nightly_input(self):
        dispatch = WORKFLOW.split("workflow_dispatch:", 1)[1].split("workflow_call:", 1)[0]
        self.assertIn(
            "      nightly_dispatch_id:\n"
            "        description: Nightly orchestrator correlation ID (nightly-certification-<run>-<attempt>),"
            " recorded in each executed evidence receipt\n"
            "        required: false\n"
            "        type: string\n", dispatch)

    def test_run_name_is_the_exact_title_honua_evidence_authenticates(self):
        self.assertIn(f"name: {TITLE}\n", WORKFLOW)
        self.assertIn(
            "run-name: ${{ inputs.nightly_dispatch_id && format('"
            f"{TITLE} [{{0}}]', inputs.nightly_dispatch_id) || '{TITLE}' }}}}\n", WORKFLOW)

    def test_dispatch_id_is_plumbed_to_the_admission_check_and_validator(self):
        self.assertIn("HONUA_NIGHTLY_DISPATCH_ID: ${{ inputs.nightly_dispatch_id || '' }}", WORKFLOW)
        self.assertIn("run: python3 scripts/conformance/cng/producer_run.py", WORKFLOW)
        self.assertIn('--nightly-dispatch-id "$HONUA_NIGHTLY_DISPATCH_ID"', WORKFLOW)
        # The admission check precedes the toolchain install and the lane itself.
        self.assertLess(WORKFLOW.index("producer_run.py"), WORKFLOW.index("Install validator toolchain"))

    def test_artifact_keeps_its_prefix_and_names_the_exact_run_attempt(self):
        self.assertIn("name: cng-conformance-results-${{ github.run_id }}-${{ github.run_attempt }}\n", WORKFLOW)
        self.assertNotIn("cng-conformance-results-${{ github.run_number }}", WORKFLOW)


class ProducerRunTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(PRODUCER_RUN, "scripts/conformance/cng/producer_run.py is missing")

    def test_dispatched_run_yields_exactly_the_five_ledger_fields(self):
        run = PRODUCER_RUN.producer_run(DISPATCH, dispatch_env())
        self.assertEqual(EXPECTED_RUN, run)
        self.assertEqual(tuple(run), PRODUCER_RUN.PRODUCER_RUN_FIELDS)
        self.assertIs(type(run["run_id"]), int)
        self.assertIs(type(run["run_attempt"]), int)

    def test_branch_workflow_ref_is_admitted(self):
        env = dispatch_env(GITHUB_WORKFLOW_REF=(
            "honua-io/honua-server/.github/workflows/cng-conformance.yml@refs/heads/trunk"))
        self.assertEqual(EXPECTED_RUN, PRODUCER_RUN.producer_run(DISPATCH, env))

    def test_undispatched_run_emits_no_identity(self):
        self.assertIsNone(PRODUCER_RUN.producer_run("", dispatch_env(GITHUB_EVENT_NAME="schedule")))
        self.assertIsNone(PRODUCER_RUN.producer_run(None, {}))

    def test_malformed_identity_or_foreign_run_is_refused(self):
        cases = {
            "bad id": ("nightly-certification-0-1", {}),
            "bad id suffix": (DISPATCH + "\n", {}),
            "bad id prefix": ("certification-1-1", {}),
            "oversized id": ("nightly-certification-" + "1" * 120 + "-1", {}),
            "wrong event": (DISPATCH, {"GITHUB_EVENT_NAME": "schedule"}),
            "pull request": (DISPATCH, {"GITHUB_EVENT_NAME": "pull_request"}),
            "wrong repo": (DISPATCH, {"GITHUB_REPOSITORY": "someone/honua-server"}),
            "other workflow": (DISPATCH, {"GITHUB_WORKFLOW_REF":
                "honua-io/honua-server/.github/workflows/cng-candidate.yml@refs/heads/trunk"}),
            "foreign workflow ref": (DISPATCH, {"GITHUB_WORKFLOW_REF":
                "someone/honua-server/.github/workflows/cng-conformance.yml@refs/heads/trunk"}),
            "pull ref": (DISPATCH, {"GITHUB_WORKFLOW_REF":
                "honua-io/honua-server/.github/workflows/cng-conformance.yml@refs/pull/1/merge"}),
            "zero attempt": (DISPATCH, {"GITHUB_RUN_ATTEMPT": "0"}),
            "missing run": (DISPATCH, {"GITHUB_RUN_ID": ""}),
        }
        for name, (dispatch_id, overrides) in cases.items():
            with self.subTest(name), self.assertRaises(ValueError):
                PRODUCER_RUN.producer_run(dispatch_id, dispatch_env(**overrides))

    def test_admission_script_fails_closed(self):
        with unittest.mock.patch.dict(
                "os.environ", {**dispatch_env(GITHUB_EVENT_NAME="push"),
                               "HONUA_NIGHTLY_DISPATCH_ID": DISPATCH}, clear=True):
            self.assertEqual(1, PRODUCER_RUN.main())
        with unittest.mock.patch.dict(
                "os.environ", {**dispatch_env(), "HONUA_NIGHTLY_DISPATCH_ID": DISPATCH}, clear=True):
            self.assertEqual(0, PRODUCER_RUN.main())


class ValidatorProducerRunTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(hasattr(VALIDATOR, "_bind_producer_run"),
                        "validator does not bind producer_run into receipts")

    def run_validator(self, env: dict[str, str], *extra: str) -> tuple[int, dict | None]:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "artifacts").mkdir()
            (root / "native").mkdir()
            output = root / "fragment.json"
            argv = [
                "validate-canonical-artifacts.py",
                "--artifacts", str(root / "artifacts"),
                "--native-results", str(root / "native"),
                "--output", str(output),
                "--source-sha", "a" * 40,
                "--image-digest", "sha256:" + "b" * 64,
                "--candidate-cut-at", "2026-10-26T00:00:00Z",
                "--fixture-revision", "fixture-v1",
                "--evidence-uri", "https://example.test/run",
                "--base-url", "http://localhost:1",
                *extra,
            ]
            # No cell executes: the contract under test is the fragment identity.
            with unittest.mock.patch.object(sys, "argv", argv), \
                    unittest.mock.patch.dict("os.environ", env, clear=True), \
                    unittest.mock.patch.object(VALIDATOR, "_collect", lambda *a, **k: None):
                try:
                    code = VALIDATOR.main()
                except SystemExit as error:
                    return error.code, None
            return code, json.loads(output.read_text(encoding="utf-8"))

    def test_dispatched_fragment_states_the_exact_producer_run(self):
        _code, fragment = self.run_validator(dispatch_env(), "--nightly-dispatch-id", DISPATCH)
        self.assertEqual(EXPECTED_RUN, fragment["producer_run"])

    def test_dispatch_id_is_read_from_the_lane_environment(self):
        _code, fragment = self.run_validator(
            {**dispatch_env(), "HONUA_NIGHTLY_DISPATCH_ID": DISPATCH})
        self.assertEqual(EXPECTED_RUN, fragment["producer_run"])

    def test_undispatched_fragment_is_unchanged(self):
        _code, fragment = self.run_validator(dispatch_env(GITHUB_EVENT_NAME="schedule"))
        self.assertNotIn("producer_run", fragment)
        self.assertTrue(all(row["evidence_receipt"] is None for row in fragment["observations"]))

    def test_validator_refuses_a_bad_dispatch_before_writing_evidence(self):
        for env, dispatch_id in (
                (dispatch_env(GITHUB_EVENT_NAME="schedule"), DISPATCH),
                (dispatch_env(), "nightly-certification-1"),
                (dispatch_env(GITHUB_REPOSITORY="fork/honua-server"), DISPATCH)):
            with self.subTest(env=env.get("GITHUB_EVENT_NAME"), dispatch_id=dispatch_id):
                code, fragment = self.run_validator(env, "--nightly-dispatch-id", dispatch_id)
                self.assertNotEqual(0, code)
                self.assertIsNone(fragment)

    def test_executed_receipt_identity_carries_producer_run(self):
        receipt = {"schema": "honua.certification-evidence-receipt/v2",
                   "identity": {"capability_key": "cng.cog.read"}}
        rows = [
            {"surface": "cog", "operation": "read", "canonical_client": "Rasterio",
             "result": "pass", "evidence_receipt": receipt},
            {"surface": "zarr", "operation": "read", "canonical_client": "zarr",
             "result": "skip", "evidence_receipt": None},
        ]
        VALIDATOR._bind_producer_run(rows, dict(EXPECTED_RUN))
        self.assertEqual(EXPECTED_RUN, receipt["identity"]["producer_run"])
        self.assertIsNone(rows[1]["evidence_receipt"])
        VALIDATOR._bind_producer_run(rows, None)  # not dispatched: nothing to bind
        with self.assertRaises(ValueError):
            VALIDATOR._bind_producer_run(
                [{**rows[0], "evidence_receipt": {"identity": None}}], dict(EXPECTED_RUN))


if __name__ == "__main__":
    unittest.main()
