"""Release acquisition boundary regressions; no network, tools or extraction."""
import base64
import hashlib
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import assemble_workspace_image as assembly


class DefaultDependencyIntegrityTests(unittest.TestCase):
    def test_corrupted_download_never_enters_the_restore_feed(self):
        expected = base64.b64encode(hashlib.sha512(b"reviewed package").digest()).decode()
        with tempfile.TemporaryDirectory() as root, patch.object(assembly, "download", return_value=b"corrupted package"):
            with self.assertRaisesRegex(ValueError, "before extraction"):
                assembly.prepare_verified_nuget_feed({("sample", "1.0.0"): expected}, Path(root))
            self.assertEqual(list(Path(root).iterdir()), [])

    def test_verified_raw_bytes_are_the_only_feed_artifact(self):
        data = b"reviewed package"
        expected = base64.b64encode(hashlib.sha512(data).digest()).decode()
        with tempfile.TemporaryDirectory() as root, patch.object(assembly, "download", return_value=data) as acquire:
            assembly.prepare_verified_nuget_feed({("sample", "1.0.0"): expected}, Path(root))
            self.assertEqual((Path(root) / "sample.1.0.0.nupkg").read_bytes(), data)
            self.assertEqual(acquire.call_args.args[0], "https://api.nuget.org/v3-flatcontainer/sample/1.0.0/sample.1.0.0.nupkg")

    def test_lock_cannot_choose_an_arbitrary_url_or_destination(self):
        with tempfile.TemporaryDirectory() as root, patch.object(assembly, "download") as acquire:
            with self.assertRaisesRegex(ValueError, "Invalid dependency"):
                assembly.prepare_verified_nuget_feed({("../host", "1.0.0"): "ignored"}, Path(root))
            acquire.assert_not_called()


if __name__ == "__main__":
    unittest.main()
