from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from scripts.conformance.cite.write_wps20_provenance import write_provenance


class WriteWps20ProvenanceTests(unittest.TestCase):
    def test_writes_linked_source_and_image_identity(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            image_id = "sha256:" + ("b" * 64)
            inspect_path = root / "honua-server-image-inspect.json"
            inspect_path.write_text(json.dumps([{"Id": image_id}]), encoding="utf-8")
            output_path = root / "honua-server-provenance.json"

            write_provenance(
                "a" * 40,
                "a" * 40,
                "d" * 64,
                image_id,
                "source-build",
                "",
                inspect_path,
                output_path,
                True,
            )

            payload = json.loads(output_path.read_text(encoding="utf-8"))
            self.assertEqual(payload["testedHonuaGitSha"], "a" * 40)
            self.assertEqual(payload["serverImageId"], image_id)
            self.assertEqual(payload["serverImageInspectFile"], inspect_path.name)
            self.assertEqual(payload["serverBuildMode"], "source-build")

    def test_required_git_sha_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            image_id = "sha256:" + ("b" * 64)
            inspect_path = root / "inspect.json"
            inspect_path.write_text(json.dumps([{"Id": image_id}]), encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "full tested Honua git SHA"):
                write_provenance(
                    "unknown", "unknown", "d" * 64, image_id, "source-build", "", inspect_path, root / "out.json", True
                )

    def test_mismatched_checkout_sha_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            image_id = "sha256:" + ("b" * 64)
            inspect_path = root / "inspect.json"
            inspect_path.write_text(json.dumps([{"Id": image_id}]), encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "does not match"):
                write_provenance(
                    "a" * 40, "e" * 40, "d" * 64, image_id, "source-build", "", inspect_path, root / "out.json", True
                )

    def test_mismatched_image_inspection_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            image_id = "sha256:" + ("b" * 64)
            inspect_path = root / "inspect.json"
            inspect_path.write_text(json.dumps([{"Id": "sha256:" + ("c" * 64)}]), encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "does not match"):
                write_provenance(
                    "a" * 40,
                    "a" * 40,
                    "d" * 64,
                    image_id,
                    "source-build",
                    "",
                    inspect_path,
                    root / "out.json",
                    True,
                )

    def test_prebuilt_image_records_requested_reference_and_digest(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            image_id = "sha256:" + ("b" * 64)
            digest = "ghcr.io/honua-io/honua-server@sha256:" + ("c" * 64)
            inspect_path = root / "inspect.json"
            inspect_path.write_text(json.dumps([{"Id": image_id, "RepoDigests": [digest]}]), encoding="utf-8")
            output_path = root / "out.json"

            write_provenance(
                "a" * 40,
                "a" * 40,
                "d" * 64,
                image_id,
                "prebuilt",
                digest,
                inspect_path,
                output_path,
                True,
            )

            payload = json.loads(output_path.read_text(encoding="utf-8"))
            self.assertEqual(payload["requestedServerImage"], digest)
            self.assertEqual(payload["serverImageRepoDigests"], [digest])

    def test_required_provenance_rejects_local_existing_image(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            image_id = "sha256:" + ("b" * 64)
            inspect_path = root / "inspect.json"
            inspect_path.write_text(json.dumps([{"Id": image_id}]), encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "untracked local-existing image"):
                write_provenance(
                    "a" * 40,
                    "a" * 40,
                    "d" * 64,
                    image_id,
                    "local-existing",
                    "",
                    inspect_path,
                    root / "out.json",
                    True,
                )


if __name__ == "__main__":
    unittest.main()
