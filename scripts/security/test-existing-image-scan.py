#!/usr/bin/env python3
"""Negative controls for immutable vulnerability scan identity and verdicts."""

import copy
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("scan", Path(__file__).with_name("existing-image-scan.py"))
scan = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scan)
IMAGE = "ghcr.io/honua-io/honua-server@sha256:" + "a" * 64
REVISION = "b" * 40
INSPECTED = [{"RepoDigests": [IMAGE], "Os": "linux", "Architecture": "amd64",
              "Id": "sha256:" + "c" * 64,
              "Config": {"Labels": {"org.opencontainers.image.revision": REVISION}}}]


class ExistingImageScanTests(unittest.TestCase):
    def test_rejects_mutable_tag_and_other_repository(self):
        for image in ("ghcr.io/honua-io/honua-server:nightly", IMAGE.replace("honua-server", "other")):
            with self.assertRaises(ValueError):
                scan.validate(image, "linux/amd64", REVISION)

    def test_rejects_missing_revision_and_invalid_platform(self):
        for platform, revision in (("linux/amd64", ""), ("windows/amd64", REVISION)):
            with self.assertRaises(ValueError):
                scan.validate(IMAGE, platform, revision)

    def test_binds_digest_platform_and_source(self):
        self.assertEqual(REVISION, scan.bind_identity(IMAGE, "linux/amd64", REVISION, INSPECTED)["sourceRevision"])
        for key, value in (("RepoDigests", []), ("Architecture", "arm64"), ("Config", {})):
            changed = copy.deepcopy(INSPECTED)
            changed[0][key] = value
            with self.assertRaises(ValueError):
                scan.bind_identity(IMAGE, "linux/amd64", REVISION, changed)

    def test_rejects_unbound_or_missing_inventory(self):
        identity = scan.bind_identity(IMAGE, "linux/amd64", REVISION, INSPECTED)
        for inventory in ({}, {"Metadata": {"RepoDigests": [IMAGE], "ImageID": "wrong"}},
                          {"Metadata": {"RepoDigests": [IMAGE], "ImageID": identity["imageId"]}, "Results": []}):
            with self.assertRaises(ValueError):
                scan.verdict(identity, inventory)

    def test_retains_unfixed_findings_and_fails_fixed_high(self):
        identity = scan.bind_identity(IMAGE, "linux/amd64", REVISION, INSPECTED)
        inventory = {"Metadata": {"RepoDigests": [IMAGE], "ImageID": identity["imageId"]},
                     "Results": [{"Vulnerabilities": [{"Severity": "HIGH", "FixedVersion": ""}]}]}
        self.assertEqual("pass", scan.verdict(identity, inventory)["status"])
        self.assertEqual(1, scan.verdict(identity, inventory)["findingCount"])
        inventory["Results"][0]["Vulnerabilities"].append({"Severity": "HIGH", "FixedVersion": "1.2.3"})
        result = scan.verdict(identity, inventory)
        self.assertEqual("fail", result["status"])
        self.assertEqual(2, result["findingCount"])
        self.assertEqual(1, len(result["blockingFindings"]))


if __name__ == "__main__":
    unittest.main()
