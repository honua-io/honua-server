"""Published-artifact regression for the shipping scene; no server or native client."""
import importlib.util
import json
from pathlib import Path
import shutil
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("scene_publication", ROOT / "scripts/ci/verify-scene-fixture-publication.py")
VERIFIER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFIER)
SOURCE = ROOT / "src/Honua.Server/fixtures/scenes/downtown-honolulu"


class SceneFixturePublicationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.source = self.root / "source"
        shutil.copytree(SOURCE, self.source)
        self.publish = self.root / "publish"
        self.published = self.publish / VERIFIER.FIXTURE
        shutil.copytree(self.source, self.published)

    def test_complete_published_scene_matches_source(self):
        result = VERIFIER.verify(self.publish, self.source)
        self.assertEqual(set(result), {"tileset.json", "metadata/scene.json", "tiles/0.b3dm"})
        self.assertEqual(result["tiles/0.b3dm"]["bytes"], 968)
        self.assertEqual(result["tiles/0.b3dm"]["sha256"], "0bbfbf05927e85c28c08cca0e1f15a15dbf06c20b5d703404d8529610683ee63")

    def test_original_json_only_publish_fails(self):
        (self.published / "tiles/0.b3dm").unlink()
        with self.assertRaisesRegex(ValueError, "Missing.*tiles/0.b3dm"):
            VERIFIER.verify(self.publish, self.source)

    def test_changed_binary_fails(self):
        (self.published / "tiles/0.b3dm").write_bytes(b"different geometry")
        with self.assertRaisesRegex(ValueError, "differs.*tiles/0.b3dm"):
            VERIFIER.verify(self.publish, self.source)

    def test_published_tileset_drift_fails(self):
        (self.published / "tileset.json").write_bytes(b"{}")
        with self.assertRaisesRegex(ValueError, "differs.*tileset.json"):
            VERIFIER.verify(self.publish, self.source)

    def test_reference_cannot_escape_fixture(self):
        tileset = json.loads((self.source / "tileset.json").read_bytes())
        tileset["root"]["content"]["uri"] = "../other.b3dm"
        (self.source / "tileset.json").write_text(json.dumps(tileset), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "local relative"):
            VERIFIER.verify(self.publish, self.source)


if __name__ == "__main__":
    unittest.main()
