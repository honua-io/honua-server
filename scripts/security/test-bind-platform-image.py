#!/usr/bin/env python3
"""Controls for resolving a published image index down to one runnable manifest."""

import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

import release_images as ri

ROOT = Path(__file__).resolve().parents[2]
REPOSITORY = "ghcr.io/honua-io/honua-server@"
REQUESTED = REPOSITORY + "sha256:" + "b" * 64
PLATFORM = REPOSITORY + "sha256:" + "a" * 64
ARM = REPOSITORY + "sha256:" + "d" * 64
REVISION = "e" * 40
MANIFEST = "application/vnd.oci.image.manifest.v1+json"
ATTESTATION = "vnd.docker.reference.type"


def digest(char):
    return "sha256:" + char * 64


def entry(char, platform, extras=None):
    os_name, arch = platform.split("/", 1)
    item = {"mediaType": MANIFEST, "digest": digest(char),
            "platform": {"os": os_name, "architecture": arch}}
    if not extras:
        return item
    return {**item, **extras}


def index(manifests, media_type="application/vnd.oci.image.index.v1+json"):
    return {"mediaType": media_type, "manifests": manifests}


def inspected(image):
    return [{"RepoDigests": [image], "Os": "linux", "Architecture": "amd64",
             "Id": digest("c"),
             "Config": {"Labels": {"org.opencontainers.image.revision": REVISION}}}]


class ResolveScanImage(unittest.TestCase):
    def test_docker_manifest_list_selects_arm64_and_skips_its_attestation(self):
        document = index([
            entry("a", "linux/amd64"),
            entry("d", "linux/arm64"),
            entry("9", "linux/arm64", {"annotations": {ATTESTATION: "attestation-manifest"}}),
        ], "application/vnd.docker.distribution.manifest.list.v2+json")
        self.assertEqual(ARM, ri.resolve_scan_image(document, REQUESTED, "linux/arm64"))

    def test_other_annotations_do_not_hide_the_runnable_manifest(self):
        document = index([entry("a", "linux/amd64", {"annotations": {"org.opencontainers.image.ref.name": "server"}})])
        self.assertEqual(PLATFORM, ri.resolve_scan_image(document, REQUESTED, "linux/amd64"))

    def test_duplicate_or_unreadable_platform_entries_fail(self):
        cases = [
            index([entry("a", "linux/amd64"), entry("d", "linux/amd64")]),
            index([entry("a", "linux/amd64")], media_type="application/json"),
            index([entry("a", "linux/amd64", {"mediaType": "application/vnd.in-toto+json"})]),
            {"manifests": [entry("a", "linux/amd64")]},
            [],
            None,
            index([dict(entry("a", "linux/amd64"), digest="sha256:short")]),
        ]
        for document in cases:
            with self.subTest(document=document):
                with self.assertRaises(ValueError):
                    ri.resolve_scan_image(document, REQUESTED, "linux/amd64")

    def test_refuses_a_mutable_tag_and_a_foreign_repository(self):
        document = index([entry("a", "linux/amd64")])
        for image in ("ghcr.io/honua-io/honua-server:nightly",
                      REQUESTED.replace("honua-server", "other")):
            with self.subTest(image=image):
                with self.assertRaises(ValueError):
                    ri.resolve_scan_image(document, image, "linux/amd64")

    def test_command_prints_the_platform_ref_and_fails_closed(self):
        document = index([entry("a", "linux/amd64")])
        completed = subprocess.run(
            ["python3", "scripts/security/release_images.py", "resolve-scan-image",
             "--image", REQUESTED, "--platform", "linux/amd64"],
            input=json.dumps(document), text=True, capture_output=True, check=True, cwd=ROOT)
        self.assertEqual(PLATFORM, completed.stdout.strip())
        missing = subprocess.run(
            ["python3", "scripts/security/release_images.py", "resolve-scan-image",
             "--image", REQUESTED, "--platform", "linux/arm64"],
            input=json.dumps(document), text=True, capture_output=True, cwd=ROOT)
        self.assertEqual(1, missing.returncode)
        self.assertIn("linux/arm64", missing.stderr)
        self.assertEqual("", missing.stdout.strip())


DOCKER = '''docker() {
  if [[ "${1-}" == login ]]; then return 0; fi
  if [[ "${1-} ${2-} ${3-} ${4-}" == "buildx imagetools inspect --raw" ]]; then printf '%s' "$SOURCE_MANIFEST"; return 0; fi
  if [[ "${1-}" == pull ]]; then return 0; fi
  if [[ "${1-} ${2-}" == "image inspect" ]]; then printf '%s' "$INSPECT_JSON"; return 0; fi
  echo "unexpected docker $*" >&2
  return 1
}
'''


def run_bind(source, inspected_image, platform="linux/amd64"):
    with tempfile.TemporaryDirectory() as directory:
        work = Path(directory)
        (work / "scripts").symlink_to(ROOT / "scripts", target_is_directory=True)
        environment = dict(os.environ, SCAN_IMAGE=REQUESTED, SCAN_PLATFORM=platform, SCAN_REVISION=REVISION,
                           SOURCE_MANIFEST=json.dumps(source), INSPECT_JSON=json.dumps(inspected(inspected_image)),
                           REGISTRY_TOKEN="unused", GITHUB_ACTOR="actor", RUNNER_TEMP=str(work),
                           GITHUB_ENV=str(work / "env"), GITHUB_SERVER_URL="https://github.com",
                           GITHUB_REPOSITORY="honua-io/honua-server", GITHUB_RUN_ID="9")
        completed = subprocess.run(
            ["bash", "-c", DOCKER + "export -f docker\nbash scripts/security/bind-platform-image.sh"],
            cwd=work, env=environment, text=True, capture_output=True)
        env_path = work / "env"
        saved = env_path.read_text() if env_path.exists() else ""
        identity_path = work / "existing-image-identity.json"
        identity = json.loads(identity_path.read_text()) if identity_path.exists() else None
        return completed, saved, identity


class BindPlatformImage(unittest.TestCase):
    def test_index_records_both_digests_and_inspects_only_the_platform_manifest(self):
        document = index([entry("a", "linux/amd64"),
                          entry("9", "linux/amd64", {"annotations": {ATTESTATION: "attestation-manifest"}})])
        completed, saved, identity = run_bind(document, PLATFORM)
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertIn("REQUESTED_IMAGE=" + REQUESTED, saved)
        self.assertIn("RESOLVED_IMAGE=" + PLATFORM, saved)
        self.assertNotIn("SCAN_IMAGE=", saved)
        self.assertEqual(PLATFORM, identity["image"])
        self.assertEqual(REQUESTED, identity["requestedImage"])
        self.assertEqual(REVISION, identity["sourceRevision"])

    def test_single_manifest_keeps_the_named_digest_without_a_second_identity(self):
        document = {"schemaVersion": 2, "mediaType": MANIFEST}
        completed, saved, identity = run_bind(document, REQUESTED)
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertIn("RESOLVED_IMAGE=" + REQUESTED, saved)
        self.assertEqual(REQUESTED, identity["image"])
        self.assertNotIn("requestedImage", identity)

    def test_missing_platform_fails_before_any_job_environment_is_published(self):
        completed, saved, identity = run_bind(index([entry("a", "linux/amd64")]), PLATFORM, "linux/arm64")
        self.assertEqual(1, completed.returncode)
        self.assertEqual("", saved)
        self.assertIsNone(identity)

    def test_both_scan_workflows_call_the_shared_step_and_scan_the_resolved_image(self):
        nightly = (ROOT / ".github/workflows/security-nightly.yml").read_text()
        release = (ROOT / ".github/workflows/release-security-assets.yml").read_text()
        script = (ROOT / "scripts/security/bind-platform-image.sh").read_text()
        for text in (nightly, release):
            self.assertEqual(1, text.count("bash scripts/security/bind-platform-image.sh"))
        self.assertIn("image-ref: ${{ env.RESOLVED_IMAGE }}", nightly)
        self.assertIn("image-ref: ${{ env.RESOLVED_IMAGE }}", release)
        persisted = script.split('>> "$GITHUB_ENV"', 1)[0].rsplit('{', 1)[1]
        self.assertNotIn("SCAN_IMAGE=", persisted)
        self.assertNotIn('echo "SCAN_IMAGE=', script)


if __name__ == "__main__":
    unittest.main()
