#!/usr/bin/env python3
"""Controls for the supported-image inventory, OpenVEX generation and release scan verdicts."""

import copy
import datetime
import importlib.util
import json
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("release_images", Path(__file__).with_name("release_images.py"))
ri = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ri)

IMAGE = "ghcr.io/honua-io/honua-server@sha256:" + "a" * 64
OTHER = "ghcr.io/honua-io/honua-server@sha256:" + "d" * 64
REVISION = "b" * 40
ENTRY = {"release": "2026.1-rc.2", "image": IMAGE, "platform": "linux/amd64",
         "sourceRevision": REVISION, "registeredAt": "2026-11-30"}
IDENTITY = {"image": IMAGE, "platform": "linux/amd64", "sourceRevision": REVISION,
            "imageId": "sha256:" + "c" * 64}
STATEMENT = {"vulnerability": "CVE-2025-5222", "packages": ["pkg:deb/ubuntu/libicu74@74.2-1ubuntu3.1"],
             "status": "not_affected", "justification": "vulnerable_code_not_present",
             "impactStatement": "The vulnerable genrb tool is not installed in the image at all.",
             "evidence": "dpkg -L libicu74 lists only shared libraries; genrb is absent.",
             "reviewedAt": "2026-11-11"}
SOURCE = {"schemaVersion": 1, "author": "maintainers", "statements": [STATEMENT]}


def inventory(*entries):
    return {"schemaVersion": 1, "unsupportedReleases": ["2026.1-rc.1"], "images": [dict(e) for e in entries]}


def trivy(vulnerabilities, suppressed=()):
    return {"Metadata": {"RepoDigests": [IMAGE], "ImageID": IDENTITY["imageId"]},
            "Results": [{"Target": "ubuntu", "Vulnerabilities": list(vulnerabilities),
                         "ExperimentalModifiedFindings": list(suppressed)}]}


def finding(cve, severity, fixed=""):
    return {"VulnerabilityID": cve, "PkgName": "pkg", "InstalledVersion": "1", "FixedVersion": fixed,
            "Severity": severity}


class CommittedFiles(unittest.TestCase):
    def test_committed_inventory_and_vex_source_are_valid(self):
        ri.validate_inventory(json.loads(ri.INVENTORY.read_text()))
        self.assertTrue(ri.validate_vex_source(json.loads(ri.VEX_SOURCE.read_text())))

    def test_pre_rc2_releases_stay_unsupported(self):
        unsupported = json.loads(ri.INVENTORY.read_text())["unsupportedReleases"]
        self.assertIn("2026.1-rc.1", unsupported)


class Inventory(unittest.TestCase):
    def test_rejects_unsupported_mutable_partial_and_duplicate_entries(self):
        cases = [dict(ENTRY, release="2026.1-rc.1"), dict(ENTRY, image="ghcr.io/honua-io/honua-server:rc"),
                 dict(ENTRY, image=IMAGE.replace("honua-server", "other")), dict(ENTRY, sourceRevision="abc"),
                 dict(ENTRY, platform="linux/386"), dict(ENTRY, extra=True)]
        for case in cases:
            with self.assertRaises(ValueError, msg=case):
                ri.validate_inventory(inventory(case))
        with self.assertRaises(ValueError):
            ri.validate_inventory(inventory(ENTRY, ENTRY))

    def test_empty_inventory_yields_empty_matrix(self):
        self.assertEqual(({"include": []}, 0), ri.inventory_matrix(inventory()))

    def test_matrix_carries_a_stable_key(self):
        matrix, count = ri.inventory_matrix(inventory(ENTRY))
        self.assertEqual(1, count)
        self.assertEqual("2026.1-rc.2-amd64-aaaaaaaaaaaa", matrix["include"][0]["key"])

    def test_add_is_idempotent_and_refuses_conflicting_identity(self):
        def entry(registered_at):
            return {"release": "2026.1-rc.2", "image": IMAGE, "platform": "linux/amd64",
                    "sourceRevision": REVISION, "registeredAt": registered_at}

        inv = inventory()
        self.assertTrue(ri.inventory_add(inv, entry("2026-11-30")))
        self.assertFalse(ri.inventory_add(inv, entry("2026-12-01")))
        self.assertEqual(1, len(inv["images"]))
        with self.assertRaises(ValueError):
            ri.inventory_add(inv, dict(entry("2026-11-30"), sourceRevision="e" * 40))
        with self.assertRaises(ValueError):
            ri.inventory_add(inventory(), dict(entry("2026-11-30"), release="2026.1-rc.1"))


class Vex(unittest.TestCase):
    def test_rejects_weak_or_ambiguous_decisions(self):
        cases = [dict(STATEMENT, status="under_investigation"), dict(STATEMENT, justification="trust me"),
                 dict(STATEMENT, impactStatement="not used"), dict(STATEMENT, evidence=""),
                 dict(STATEMENT, packages=["pkg:deb/ubuntu/libicu74"]),
                 dict(STATEMENT, packages=["pkg:deb/ubuntu/passwd@1:4.13"]),
                 dict(STATEMENT, packages=["pkg:deb/ubuntu/passwd@1%3A4.13"]),
                 dict(STATEMENT, vulnerability="CVE-BAD")]
        for case in cases:
            with self.assertRaises(ValueError, msg=case):
                ri.validate_vex_source(dict(SOURCE, statements=[case]))

    def test_release_document_scopes_decisions_to_the_release_digests(self):
        doc = ri.generate_openvex(SOURCE, [IMAGE, OTHER], "2026-11-30T00:00:00Z", "2026.1-rc.2")
        self.assertEqual("https://openvex.dev/ns/v0.2.0", doc["@context"])
        products = doc["statements"][0]["products"]
        self.assertEqual(["pkg:oci/honua-server@sha256%3A" + "a" * 64
                          + "?repository_url=ghcr.io%2Fhonua-io%2Fhonua-server",
                          "pkg:oci/honua-server@sha256%3A" + "d" * 64
                          + "?repository_url=ghcr.io%2Fhonua-io%2Fhonua-server"],
                         [p["@id"] for p in products])
        self.assertEqual([{"@id": STATEMENT["packages"][0]}], products[0]["subcomponents"])
        self.assertEqual(STATEMENT["impactStatement"], doc["statements"][0]["impact_statement"])

    def test_package_document_applies_to_any_scanned_artifact(self):
        doc = ri.generate_openvex(SOURCE, [], "2026-11-30T00:00:00Z", "trunk")
        self.assertEqual([{"@id": STATEMENT["packages"][0]}], doc["statements"][0]["products"])

    def test_document_id_is_deterministic_and_content_bound(self):
        first = ri.generate_openvex(SOURCE, [IMAGE], "2026-11-30T00:00:00Z", "r")["@id"]
        self.assertEqual(first, ri.generate_openvex(SOURCE, [IMAGE], "2026-12-01T00:00:00Z", "r")["@id"])
        self.assertNotEqual(first, ri.generate_openvex(SOURCE, [OTHER], "2026-11-30T00:00:00Z", "r")["@id"])

    def test_products_must_be_immutable_digests(self):
        with self.assertRaises(ValueError):
            ri.generate_openvex(SOURCE, ["ghcr.io/honua-io/honua-server:rc"], "2026-11-30T00:00:00Z", "r")


class Verdicts(unittest.TestCase):
    def test_only_fixable_high_and_critical_block(self):
        result = ri.verdict(IDENTITY, ENTRY, trivy([
            finding("CVE-2026-1", "CRITICAL", "2"), finding("CVE-2026-2", "HIGH"),
            finding("CVE-2026-3", "MEDIUM", "2")]))
        self.assertEqual("fail", result["status"])
        self.assertEqual(["CVE-2026-1"], [f["VulnerabilityID"] for f in result["blockingFindings"]])

    def test_vex_suppressed_findings_are_recorded_not_hidden(self):
        suppressed = {"Type": "vulnerability", "Status": "not_affected",
                      "Statement": "vulnerable_code_not_present", "Source": "/w/release.openvex.json",
                      "Finding": finding("CVE-2025-5222", "LOW")}
        result = ri.verdict(IDENTITY, ENTRY, trivy([], [suppressed]))
        self.assertEqual("pass", result["status"])
        self.assertEqual([{"VulnerabilityID": "CVE-2025-5222", "PkgName": "pkg", "InstalledVersion": "1",
                           "FixedVersion": "", "Severity": "LOW", "status": "not_affected",
                           "justification": "vulnerable_code_not_present", "source": "release.openvex.json"}],
                         result["suppressedFindings"])

    def test_refuses_a_scan_of_a_different_image(self):
        for key, value in (("image", OTHER), ("platform", "linux/arm64"), ("sourceRevision", "e" * 40)):
            with self.assertRaises(ValueError):
                ri.verdict(dict(IDENTITY, **{key: value}), ENTRY, trivy([]))
        mismatched = trivy([])
        mismatched["Metadata"]["RepoDigests"] = [OTHER]
        with self.assertRaises(ValueError):
            ri.verdict(IDENTITY, ENTRY, mismatched)

    def test_an_index_entry_binds_to_its_scanned_platform_manifest(self):
        scanned = dict(IDENTITY, image=OTHER, requestedImage=IMAGE)
        result = ri.verdict(scanned, ENTRY, trivy([]) | {"Metadata": {
            "RepoDigests": [OTHER], "ImageID": IDENTITY["imageId"]}})
        self.assertEqual((IMAGE, OTHER), (result["image"], result["scannedImage"]))
        self.assertEqual(ri.scan_key(ENTRY), result["key"])
        stranger = "ghcr.io/honua-io/honua-server@sha256:" + "f" * 64
        with self.assertRaises(ValueError):
            ri.verdict(dict(scanned, requestedImage=stranger), ENTRY, trivy([]))

    def test_a_scan_binds_only_the_entry_it_was_asked_for(self):
        manifest_entry = dict(ENTRY, image=OTHER)
        both = inventory(ENTRY, manifest_entry)
        from_index = dict(IDENTITY, image=OTHER, requestedImage=IMAGE)
        from_manifest = dict(IDENTITY, image=OTHER)
        self.assertEqual([ENTRY], ri.registered_entries(both, from_index, ENTRY["release"]))
        self.assertEqual([manifest_entry], ri.registered_entries(both, from_manifest, ENTRY["release"]))
        self.assertEqual([], ri.registered_entries(both, from_index, "2026.1"))

    def test_report_sets_deadlines_and_keeps_details_out_of_the_public_summary(self):
        result = ri.verdict(IDENTITY, ENTRY, trivy([finding("CVE-2026-1", "CRITICAL", "2"),
                                                    finding("CVE-2026-4", "HIGH", "3")]))
        other = dict(ENTRY, image=OTHER)
        public, private = ri.report([result], [ri.scan_key(ENTRY), ri.scan_key(other)],
                                    datetime.date(2026, 12, 1))
        self.assertEqual({"scannedImages": 1, "missingScans": 1, "imagesNeedingPatch": 1,
                          "vexSuppressedFindings": 0}, public)
        self.assertNotIn("CVE", json.dumps(public))
        self.assertEqual(["2026-12-08", "2026-12-31"], [f["dueBy"] for f in private["findings"]])
        self.assertEqual(["2026-12-01", "2026-12-01"], [f["firstDetected"] for f in private["findings"]])
        self.assertEqual([ri.scan_key(other)], private["missingScans"])


class AttestationFreeCopy(unittest.TestCase):
    INDEX = {"mediaType": "application/vnd.oci.image.index.v1+json", "manifests": [
        {"mediaType": "application/vnd.oci.image.manifest.v1+json", "digest": "sha256:" + "1" * 64,
         "platform": {"os": "linux", "architecture": "amd64"}},
        {"mediaType": "application/vnd.oci.image.manifest.v1+json", "digest": "sha256:" + "2" * 64,
         "platform": {"os": "unknown", "architecture": "unknown"},
         "annotations": {"vnd.docker.reference.type": "attestation-manifest",
                         "vnd.docker.reference.digest": "sha256:" + "1" * 64}}]}

    def test_selects_the_platform_image_and_never_the_attestation(self):
        self.assertEqual("sha256:" + "1" * 64, ri.platform_manifest(self.INDEX, "linux/amd64"))
        with self.assertRaises(ValueError):
            ri.platform_manifest(self.INDEX, "linux/arm64")

    def test_resolves_an_index_to_the_runnable_manifest_and_keeps_a_single_manifest(self):
        requested = "ghcr.io/honua-io/honua-server@sha256:" + "a" * 64
        self.assertEqual("ghcr.io/honua-io/honua-server@sha256:" + "1" * 64,
                         ri.resolve_scan_image(self.INDEX, requested, "linux/amd64"))
        single = {"schemaVersion": 2, "mediaType": "application/vnd.oci.image.manifest.v1+json"}
        self.assertEqual(requested, ri.resolve_scan_image(single, requested, "linux/arm64"))
        with self.assertRaises(ValueError):
            ri.resolve_scan_image(self.INDEX, "ghcr.io/honua-io/honua-server:nightly", "linux/amd64")

    @staticmethod
    def descriptor(media_type):
        return {"mediaType": media_type, "digest": "sha256:" + "3" * 64, "size": 123}

    def image_manifest(self):
        return {"schemaVersion": 2, "mediaType": "application/vnd.oci.image.manifest.v1+json",
                "config": self.descriptor("application/vnd.oci.image.config.v1+json"),
                "layers": [self.descriptor("application/vnd.oci.image.layer.v1.tar+gzip")]}

    def test_recognized_container_image_encodings_are_accepted(self):
        for media_type in ("application/vnd.oci.image.layer.v1.tar", "application/vnd.oci.image.layer.v1.tar+gzip",
                           "application/vnd.oci.image.layer.v1.tar+zstd",
                           "application/vnd.docker.image.rootfs.diff.tar.gzip",
                           "application/vnd.docker.image.rootfs.foreign.diff.tar.gzip"):
            image = self.image_manifest()
            image["layers"][0]["mediaType"] = media_type
            self.assertTrue(ri.assert_single_manifest(image))
        image = self.image_manifest()
        image["mediaType"] = "application/vnd.docker.distribution.manifest.v2+json"
        image["config"]["mediaType"] = "application/vnd.docker.container.image.v1+json"
        image["layers"] = []
        self.assertTrue(ri.assert_single_manifest(image))

    def test_artifacts_and_malformed_image_envelopes_are_rejected(self):
        image = self.image_manifest()
        bad = [None, [], {}, dict(image, schemaVersion=1), dict(image, schemaVersion=2.0),
               dict(image, mediaType={}), dict(image, config=None),
               dict(image, layers=None), dict(image, layers=[None]),
               dict(image, subject={}), dict(image, artifactType="application/example"),
               dict(image, config=self.descriptor("application/vnd.example.config.v1+json")),
               dict(image, layers=[self.descriptor("application/json")])]
        for field, value in (("digest", "sha256:bad"), ("size", -1), ("size", True),
                             ("size", "123"), ("mediaType", {}), ("mediaType", "application/vnd.in-toto+json")):
            layer = self.descriptor("application/vnd.oci.image.layer.v1.tar")
            layer[field] = value
            bad.append(dict(image, layers=[layer]))
        for manifest in bad:
            with self.subTest(manifest=manifest), self.assertRaises(ValueError):
                ri.assert_single_manifest(manifest)

    def test_target_must_be_a_single_manifest_without_attestations(self):
        manifest = {"mediaType": "application/vnd.oci.image.manifest.v1+json",
                    "schemaVersion": 2,
                    "config": self.descriptor("application/vnd.oci.image.config.v1+json"),
                    "layers": [self.descriptor("application/vnd.oci.image.layer.v1.tar+gzip")]}
        self.assertTrue(ri.assert_single_manifest(manifest))
        attested = copy.deepcopy(manifest)
        attested["layers"].append({"mediaType": "application/vnd.in-toto+json"})
        for bad in (self.INDEX, attested, dict(manifest, subject={"digest": "sha256:" + "1" * 64})):
            with self.assertRaises(ValueError):
                ri.assert_single_manifest(bad)


if __name__ == "__main__":
    unittest.main()
