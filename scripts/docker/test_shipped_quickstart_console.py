#!/usr/bin/env python3
"""Refuse unofficial or unavailable Console artifacts in customer packages."""
import json
import unittest
from unittest.mock import patch

import fetch_quickstart_console as fetch


def run(**overrides):
    return dict(id=7, head_repository={"full_name": fetch.REPOSITORY}, head_branch="trunk",
                event="push", conclusion="success", head_sha="a" * 40, **overrides)


class ConsoleBuildTests(unittest.TestCase):
    def test_unofficial_unsuccessful_and_invalid_revisions_are_refused(self):
        for field, value in (("head_repository", None), ("head_repository", {"full_name": "fork/console"}),
                             ("head_branch", "feature"), ("event", "pull_request"),
                             ("conclusion", "failure"), ("head_sha", "invalid")):
            candidate = run()
            candidate[field] = value
            with self.subTest(field=field, value=value), patch.object(fetch, "github", return_value=json.dumps({
                    "workflow_runs": [candidate]})), self.assertRaisesRegex(ValueError, "No successful"):
                fetch.select_build()

    def test_expired_or_absent_artifacts_are_refused(self):
        for artifacts in ([], [{"name": "honua-console-web", "expired": True}],
                          [{"name": "other", "expired": False}]):
            with self.subTest(artifacts=artifacts), patch.object(fetch, "github", side_effect=[
                    json.dumps({"workflow_runs": [run()]}), json.dumps({"artifacts": artifacts})]), \
                    self.assertRaisesRegex(ValueError, "No successful"):
                fetch.select_build()

    def test_latest_eligible_official_artifact_is_bound(self):
        earlier = run()
        earlier["id"] = 6
        with patch.object(fetch, "github", side_effect=[json.dumps({"workflow_runs": [run(), earlier]}),
                json.dumps({"artifacts": []}),
                json.dumps({"artifacts": [{"name": "honua-console-web", "expired": False}]})]):
            self.assertEqual(6, fetch.select_build()["id"])


if __name__ == "__main__":
    unittest.main()
