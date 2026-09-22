"""Unpinned or missing client provenance must stop evidence publication."""
import importlib
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lib"))
identity = importlib.import_module("image_identity")


class ImageIdentityTests(unittest.TestCase):
    def test_registry_digest_is_preserved(self):
        ref = "ghcr.io/osgeo/gdal:ubuntu-full-3.13.3@sha256:" + "a" * 64
        self.assertEqual([ref], identity.pinned_base_digests(ref))

    def test_missing_mutable_or_malformed_identity_fails(self):
        for ref in ("", "ghcr.io/osgeo/gdal:ubuntu-full-3.13.3", "gdal@sha256:short"):
            with self.subTest(ref=ref), self.assertRaises(ValueError):
                identity.pinned_base_digests(ref)
