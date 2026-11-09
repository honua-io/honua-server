#!/usr/bin/env python3
"""Candidate-origin, document-source and prospective-package refusal controls."""
import base64
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import prepare_quickstart_proof as prepare
import validate_shipped_quickstart as proof


def official_run(**overrides):
    return dict({"id": 1, "head_branch": "trunk", "head_sha": "a" * 40,
                 "event": "workflow_dispatch", "head_repository": {"full_name": prepare.REPOSITORY}}, **overrides)



class CandidateTests(unittest.TestCase):
    def test_fork_and_unexpected_event_cannot_supply_candidate_inputs(self):
        for field, value in (("event", "pull_request"), ("head_repository", {"full_name": "other/server"})):
            run = official_run()
            run[field] = value
            results = [json.dumps({"workflow_runs": [run]}), json.dumps({"artifacts": [{
                "name": "honua-compose-quickstart-" + "a" * 7, "expired": False}]})]
            with self.subTest(field=field), patch.object(prepare, "github", side_effect=results), \
                    self.assertRaisesRegex(ValueError, "No successful"):
                prepare.select_candidate()

    def test_only_official_artifact_bearing_runs_are_selected(self):
        sha = "b" * 40
        runs = [official_run(head_branch="feature"), official_run(id=2, head_sha="c" * 40),
                official_run(id=3, head_branch="nightly-candidate-" + sha[:7], head_sha=sha)]
        results = [json.dumps({"workflow_runs": runs}), json.dumps({"artifacts": []}),
                   json.dumps({"artifacts": [{"name": "honua-compose-quickstart-" + sha[:7], "expired": False}]})]
        with patch.object(prepare, "github", side_effect=results):
            run, name = prepare.select_candidate()
        self.assertEqual(3, run["id"])
        self.assertEqual("honua-compose-quickstart-" + sha[:7], name)

    def test_missing_or_expired_candidate_package_is_not_a_pass(self):
        for artifacts in ([], [{"name": "honua-compose-quickstart-" + "a" * 7, "expired": True}]):
            with self.subTest(artifacts=artifacts):
                results = [json.dumps({"workflow_runs": [official_run()]}),
                           json.dumps({"artifacts": artifacts})]
                with patch.object(prepare, "github", side_effect=results), self.assertRaisesRegex(ValueError, "No successful"):
                    prepare.select_candidate()

    def test_non_hex_source_is_not_eligible(self):
        with patch.object(prepare, "github", return_value=json.dumps({
                "workflow_runs": [official_run(head_sha="z" * 40)]})), self.assertRaises(ValueError):
            prepare.select_candidate()



class PrepareTests(unittest.TestCase):
    def test_existing_output_and_invalid_prospective_source_are_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            for arguments in (["--output", directory], ["--output", str(Path(directory) / "new"),
                                                      "--prospective-source-sha", "invalid"]):
                with self.subTest(arguments=arguments), patch.object(proof.sys, "argv", ["prepare", *arguments]), \
                        self.assertRaises(ValueError):
                    prepare.main()

    def test_document_source_binding_and_prospective_checkout_refusal(self):
        for prospective in (False, True):
            with self.subTest(prospective=prospective), tempfile.TemporaryDirectory() as directory:
                output = Path(directory) / "proof"
                arguments = ["prepare", "--output", str(output)]
                if prospective:
                    arguments.extend(["--prospective-source-sha", "b" * 40])
                def github(*args):
                    if args[0] == "run":
                        target = output / "published"
                        target.mkdir()
                        archive = target / "honua-compose-quickstart.tar.gz"
                        archive.write_bytes(b"fixture")
                        archive.with_name(archive.name + ".sha256").write_text("fixture checksum")
                        return ""
                    self.assertIn("ref=" + "a" * 40, args[1])
                    return json.dumps({"content": base64.b64encode(b"source-bound document").decode()})
                with patch.object(proof.sys, "argv", arguments), \
                        patch.object(prepare, "select_candidate", return_value=(official_run(), "artifact")), \
                        patch.object(prepare, "github", side_effect=github), \
                        patch.object(prepare, "unpack", return_value={}), \
                        patch.object(prepare.subprocess, "check_output", return_value="c" * 40):
                    if prospective:
                        with self.assertRaisesRegex(ValueError, "checked-out commit"):
                            prepare.main()
                    else:
                        prepare.main()
                        self.assertEqual(b"source-bound document", (output / "quickstart.md").read_bytes())
                        self.assertEqual("candidate", json.loads((output / "input.json").read_text())["packageKind"])



if __name__ == "__main__":
    unittest.main()
