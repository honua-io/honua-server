"""The native customer journey must recognize synchronous and asynchronous completion."""
import importlib.util
import json
from pathlib import Path
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location(
    "compose_release_lifecycle", ROOT / "tests/integration/test_compose_release_lifecycle.py")
lifecycle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lifecycle)


class ProposalPollingTests(unittest.TestCase):
    def read(self, status):
        return {"status": status, "resolutionReason": None}

    def resource(self, status):
        return ({"contents": [{"text": json.dumps(self.read(status))}]}, None)

    def test_accepts_synchronous_restart_and_asynchronous_deploy_success(self):
        for status in ("Succeeded", "Completed"):
            with self.subTest(status=status):
                with patch.object(lifecycle, "request", return_value=(self.read(status), None)), \
                        patch.object(lifecycle, "rpc", return_value=self.resource(status)), \
                        patch.object(lifecycle.time, "monotonic", side_effect=[0, 0, 2]), \
                        patch.object(lifecycle.time, "sleep"):
                    detail = lifecycle.wait_for_proposal(
                        "http://manager.invalid", "fixture", "proposal", "succeeded",
                        "fixture", "session", timeout=1)
                self.assertEqual(status, detail["status"])

    def test_waits_for_rest_and_mcp_to_agree(self):
        with patch.object(lifecycle, "request", return_value=(self.read("Succeeded"), None)), \
                patch.object(lifecycle, "rpc", side_effect=[
                    self.resource("Executing"), self.resource("Succeeded")]), \
                patch.object(lifecycle.time, "monotonic", side_effect=[0, 0, 0, 2]), \
                patch.object(lifecycle.time, "sleep") as sleep:
            detail = lifecycle.wait_for_proposal(
                "http://manager.invalid", "fixture", "proposal", "succeeded",
                "fixture", "session", timeout=1)
        self.assertEqual("Succeeded", detail["status"])
        sleep.assert_called_once()

    def test_failed_execution_cannot_satisfy_success(self):
        with patch.object(lifecycle, "request", return_value=(self.read("Failed"), None)), \
                patch.object(lifecycle, "rpc", return_value=self.resource("Failed")):
            with self.assertRaisesRegex(AssertionError, "unexpected proposal terminal state: failed"):
                lifecycle.wait_for_proposal(
                    "http://manager.invalid", "fixture", "proposal", "succeeded",
                    "fixture", "session", timeout=1)


if __name__ == "__main__":
    unittest.main()
