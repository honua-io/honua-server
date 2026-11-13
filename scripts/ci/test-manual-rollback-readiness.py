"""Regression tests for host-side readiness during legacy Compose startup."""
from pathlib import Path
import sys
import unittest
from unittest.mock import MagicMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tests/integration"))
import test_compose_manual_rc1_rollback as rollback


class ManualRollbackReadinessTests(unittest.TestCase):
    def test_startup_connection_reset_is_retried(self):
        ready = MagicMock()
        ready.__enter__.return_value.status = 200
        with patch.object(rollback.urllib.request, "urlopen",
                          side_effect=[ConnectionResetError("startup"), ready]) as request, \
                patch.object(rollback.time, "sleep"):
            rollback.wait_ready(8080)
        self.assertEqual(request.call_count, 2)

    def test_persistent_connection_reset_still_times_out(self):
        with patch.object(rollback.urllib.request, "urlopen", side_effect=ConnectionResetError), \
                patch.object(rollback.time, "monotonic", side_effect=[0, 1, 301]), \
                patch.object(rollback.time, "sleep"):
            with self.assertRaisesRegex(AssertionError, "did not become ready"):
                rollback.wait_ready(8080)


if __name__ == "__main__":
    unittest.main()
