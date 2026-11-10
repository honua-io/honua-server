#!/usr/bin/env python3
"""Offline trust-boundary tests for normalization-envelope.py."""

from __future__ import annotations

import base64
import importlib.util
import json
import re
import stat
import tempfile
import unittest
from unittest.mock import patch
import zipfile
from pathlib import Path

SCRIPT = Path(__file__).with_name("normalization-envelope.py")
SPEC = importlib.util.spec_from_file_location("normalization_envelope", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

REPOSITORY = "honua-io/honua-server"
PR = 3219
HEAD = "a" * 40
TREE = "e" * 40
BASE = "b" * 40
RUN_ID = 12345
ATTEMPT = 1


def output(path: str, content: bytes = b'{"ok":true}\n') -> dict:
    return {
        "content_base64": base64.b64encode(content).decode("ascii"),
        "length": len(content),
        "path": path,
        "sha256": MODULE.sha256_bytes(content),
    }


def envelope() -> dict:
    return {
        "generators": [
            {"path": path, "sha256": "c" * 64}
            for path in MODULE.GENERATOR_INPUTS
        ],
        "outputs": [output(path) for path in MODULE.OUTPUT_LIMITS],
        "producer": {
            "event": "pull_request",
            "run_attempt": ATTEMPT,
            "run_id": RUN_ID,
            "workflow_path": MODULE.WORKFLOW_PATH,
        },
        "schema": MODULE.SCHEMA,
        "source": {
            "base_sha": BASE,
            "pull_request": PR,
            "repository": REPOSITORY,
            "sha": HEAD,
            "tree_sha": TREE,
        },
    }


def raw(value: dict) -> bytes:
    return (json.dumps(value, sort_keys=True) + "\n").encode()


def expected() -> dict:
    return {
        "repository": REPOSITORY,
        "pull_request": PR,
        "source_sha": HEAD,
        "source_tree_sha": TREE,
        "base_sha": BASE,
        "run_id": RUN_ID,
        "run_attempt": ATTEMPT,
    }


class EnvelopeTests(unittest.TestCase):
    def test_output_allowlist_contains_only_the_three_governed_projections(self) -> None:
        self.assertEqual(
            {
                "docs/gis/data/feature-catalog.json",
                "docs/gis/data/geoservices-rest-parity.json",
                "docs/gis/data/capability-matrix.v1.json",
            },
            set(MODULE.OUTPUT_LIMITS),
        )

    def test_workflow_and_envelope_enforce_the_same_bounds(self) -> None:
        workflow = (SCRIPT.parents[2] / MODULE.WORKFLOW_PATH).read_text()
        projections = re.findall(r"'(docs/gis/data/[^':]+\.json):(\d+)'", workflow)
        self.assertEqual(len(MODULE.OUTPUT_LIMITS), len(projections))
        self.assertEqual(MODULE.OUTPUT_LIMITS, {path: int(limit) for path, limit in projections})
        aggregate = re.search(r"if \(\( total > (\d+) \)\)", workflow)
        self.assertIsNotNone(aggregate)
        self.assertEqual(MODULE.MAX_TOTAL_OUTPUT_BYTES, int(aggregate.group(1)))

    def test_admin_projections_cannot_be_normalization_outputs(self) -> None:
        for path in (
            "docs/gis/data/admin-openapi-operation-ids.json",
            "docs/gis/data/admin-mcp-projection-manifest.json",
        ):
            with self.subTest(path=path):
                value = envelope()
                value["outputs"][0] = output(path)
                with self.assertRaisesRegex(MODULE.EnvelopeError, "not allowlisted"):
                    MODULE.validate_envelope(raw(value), **expected())

    def test_previous_envelope_schema_is_rejected(self) -> None:
        value = envelope()
        value["schema"] = "honua.normalization-envelope/v2"
        with self.assertRaisesRegex(MODULE.EnvelopeError, "schema"):
            MODULE.validate_envelope(raw(value), **expected())

    def test_valid_envelope_round_trips(self) -> None:
        plan = MODULE.validate_envelope(raw(envelope()), **expected())
        self.assertEqual(set(MODULE.OUTPUT_LIMITS), {item["path"] for item in plan["outputs"]})

    def test_source_commit_and_tree_identity_are_exact(self) -> None:
        value = envelope()
        value["source"]["sha"] = "d" * 40
        with self.assertRaisesRegex(MODULE.EnvelopeError, "source identity"):
            MODULE.validate_envelope(raw(value), **expected())

        value = envelope()
        value["source"]["tree_sha"] = "f" * 40
        with self.assertRaisesRegex(MODULE.EnvelopeError, "source identity"):
            MODULE.validate_envelope(raw(value), **expected())

    def test_every_producer_and_source_binding_is_exact(self) -> None:
        for section, field, replacement in (
            ("producer", "run_id", RUN_ID + 1),
            ("producer", "run_attempt", ATTEMPT + 1),
            ("producer", "workflow_path", ".github/workflows/other.yml"),
            ("source", "base_sha", "d" * 40),
            ("source", "repository", "other/repository"),
            ("source", "pull_request", PR + 1),
        ):
            with self.subTest(section=section, field=field):
                value = envelope()
                value[section][field] = replacement
                with self.assertRaisesRegex(MODULE.EnvelopeError, "identity"):
                    MODULE.validate_envelope(raw(value), **expected())

    def test_envelope_and_archive_sizes_are_bounded(self) -> None:
        with self.assertRaisesRegex(MODULE.EnvelopeError, "envelope exceeds"):
            MODULE.validate_envelope(b" " * (MODULE.MAX_ENVELOPE_BYTES + 1), **expected())
        with tempfile.TemporaryDirectory() as directory:
            archive = Path(directory) / "oversized.zip"
            archive.write_bytes(b" " * (MODULE.MAX_ARCHIVE_BYTES + 1))
            with self.assertRaisesRegex(MODULE.EnvelopeError, "archive exceeds"):
                MODULE.validate_archive(archive, **expected())

    def test_validation_enforces_the_aggregate_output_limit(self) -> None:
        value = envelope()
        actual = sum(item["length"] for item in value["outputs"])
        with patch.object(MODULE, "MAX_TOTAL_OUTPUT_BYTES", actual - 1):
            with self.assertRaisesRegex(MODULE.EnvelopeError, "outputs exceed"):
                MODULE.validate_envelope(raw(value), **expected())

    def test_encrypted_archive_members_are_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "encrypted.zip"
            with zipfile.ZipFile(path, "w") as archive:
                archive.writestr(MODULE.ARCHIVE_MEMBER, raw(envelope()))
            content = bytearray(path.read_bytes())
            content[6] |= 1
            central = content.index(b"PK\x01\x02")
            content[central + 8] |= 1
            path.write_bytes(content)
            with self.assertRaisesRegex(MODULE.EnvelopeError, "encrypted"):
                MODULE.validate_archive(path, **expected())

    def test_fork_validation_may_omit_only_unverifiable_base_identity(self) -> None:
        value = envelope()
        value["source"]["base_sha"] = "d" * 40
        fork_expected = expected()
        fork_expected["base_sha"] = None
        plan = MODULE.validate_envelope(raw(value), **fork_expected)
        self.assertEqual("d" * 40, plan["source"]["base_sha"])

        value["source"]["sha"] = "e" * 40
        with self.assertRaisesRegex(MODULE.EnvelopeError, "source identity"):
            MODULE.validate_envelope(raw(value), **fork_expected)

    def test_extra_envelope_key_is_rejected(self) -> None:
        value = envelope()
        value["command"] = "echo unsafe"
        with self.assertRaisesRegex(MODULE.EnvelopeError, "keys must be"):
            MODULE.validate_envelope(raw(value), **expected())

    def test_unallowlisted_and_duplicate_output_paths_are_rejected(self) -> None:
        value = envelope()
        value["outputs"][0]["path"] = "../.github/workflows/pr-gate.yml"
        with self.assertRaisesRegex(MODULE.EnvelopeError, "not allowlisted"):
            MODULE.validate_envelope(raw(value), **expected())

        value = envelope()
        value["outputs"][1]["path"] = value["outputs"][0]["path"]
        with self.assertRaisesRegex(MODULE.EnvelopeError, "duplicate output"):
            MODULE.validate_envelope(raw(value), **expected())

    def test_digest_length_and_base64_are_verified(self) -> None:
        for field, replacement, pattern in (
            ("sha256", "d" * 64, "length/digest"),
            ("length", 1, "length/digest"),
            ("content_base64", "not base64!", "canonical base64"),
        ):
            with self.subTest(field=field):
                value = envelope()
                value["outputs"][0][field] = replacement
                with self.assertRaisesRegex(MODULE.EnvelopeError, pattern):
                    MODULE.validate_envelope(raw(value), **expected())

    def test_output_json_rejects_duplicate_keys_nonfinite_and_invalid_utf8(self) -> None:
        for content, pattern in (
            (b'{"x":1,"x":2}', "duplicate JSON key"),
            (b'{"x":NaN}', "non-finite"),
            (b'{"x":"\xff"}', "UTF-8"),
        ):
            with self.subTest(pattern=pattern):
                value = envelope()
                value["outputs"][0] = output(value["outputs"][0]["path"], content)
                with self.assertRaisesRegex(MODULE.EnvelopeError, pattern):
                    MODULE.validate_envelope(raw(value), **expected())

    def test_output_json_must_be_an_object(self) -> None:
        value = envelope()
        value["outputs"][0] = output(value["outputs"][0]["path"], b"[]\n")
        with self.assertRaisesRegex(MODULE.EnvelopeError, "JSON object"):
            MODULE.validate_envelope(raw(value), **expected())

    def test_generator_allowlist_is_exact(self) -> None:
        required_implementations = {
            "Directory.Build.targets",
            "tests/dotnet/Honua.Architecture.SourceTests/ArchitectureTestHelpers.Source.cs",
            "scripts/ci/generate-admin-operation-parity-exports.sh",
            "scripts/ci/verify-admin-operation-parity.py",
            "tests/dotnet/Honua.Architecture.Tests/FeatureCatalog/FeatureCatalogEmitter.cs",
            "tests/dotnet/Honua.Architecture.Tests/FeatureCatalog/FeatureCatalogGenerator.cs",
            "tests/dotnet/Honua.Architecture.Tests/GeoServicesParity/GeoServicesParityEmitter.cs",
            "tests/dotnet/Honua.Architecture.Tests/GeoServicesParity/GeoServicesParityGenerator.cs",
            "tests/dotnet/Honua.Server.Tests/Features/Operations/AdminOperationParityExportTests.cs",
        }
        self.assertTrue(required_implementations.issubset(MODULE.GENERATOR_INPUTS))

        value = envelope()
        value["generators"][0]["path"] = "scripts/unsafe.sh"
        with self.assertRaisesRegex(MODULE.EnvelopeError, "allowlist/order"):
            MODULE.validate_envelope(raw(value), **expected())

    def test_packager_rejects_non_regular_inputs(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            with self.assertRaisesRegex(MODULE.EnvelopeError, "regular file"):
                MODULE.read_regular_bytes(path, "projection")

    def test_archive_requires_one_regular_named_member(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            valid = root / "valid.zip"
            with zipfile.ZipFile(valid, "w", zipfile.ZIP_DEFLATED) as archive:
                archive.writestr(MODULE.ARCHIVE_MEMBER, raw(envelope()))
            self.assertEqual(MODULE.SCHEMA, MODULE.validate_archive(valid, **expected())["schema"])

            extra = root / "extra.zip"
            with zipfile.ZipFile(extra, "w") as archive:
                archive.writestr(MODULE.ARCHIVE_MEMBER, raw(envelope()))
                archive.writestr("extra.txt", "unsafe")
            with self.assertRaisesRegex(MODULE.EnvelopeError, "contain only"):
                MODULE.validate_archive(extra, **expected())

            traversal = root / "traversal.zip"
            with zipfile.ZipFile(traversal, "w") as archive:
                archive.writestr("../normalization-envelope.json", raw(envelope()))
            with self.assertRaisesRegex(MODULE.EnvelopeError, "contain only"):
                MODULE.validate_archive(traversal, **expected())

            symlink = root / "symlink.zip"
            member = zipfile.ZipInfo(MODULE.ARCHIVE_MEMBER)
            member.create_system = 3
            member.external_attr = (stat.S_IFLNK | 0o777) << 16
            with zipfile.ZipFile(symlink, "w") as archive:
                archive.writestr(member, "target")
            with self.assertRaisesRegex(MODULE.EnvelopeError, "regular file"):
                MODULE.validate_archive(symlink, **expected())

    def test_build_binds_real_outputs_and_generator_inputs(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for path in (*MODULE.OUTPUT_LIMITS, *MODULE.GENERATOR_INPUTS):
                target = root / path
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text("{}\n" if path.endswith(".json") else "input\n", encoding="utf-8")
            built = MODULE.build_envelope(
                root, root, REPOSITORY, PR, HEAD, TREE, BASE, RUN_ID, ATTEMPT
            )
            validated = MODULE.validate_envelope(raw(built), **expected())
            self.assertEqual(len(MODULE.OUTPUT_LIMITS), len(validated["outputs"]))

            source_helper = "tests/dotnet/Honua.Architecture.SourceTests/ArchitectureTestHelpers.Source.cs"
            original_digest = next(entry["sha256"] for entry in built["generators"] if entry["path"] == source_helper)
            (root / source_helper).write_text("changed executable helper\n", encoding="utf-8")
            changed = MODULE.build_envelope(
                root, root, REPOSITORY, PR, HEAD, TREE, BASE, RUN_ID, ATTEMPT
            )
            changed_digest = next(entry["sha256"] for entry in changed["generators"] if entry["path"] == source_helper)
            self.assertNotEqual(original_digest, changed_digest)

            omitted = envelope()
            omitted["generators"] = [entry for entry in omitted["generators"] if entry["path"] != source_helper]
            with self.assertRaisesRegex(MODULE.EnvelopeError, "generator evidence count"):
                MODULE.validate_envelope(raw(omitted), **expected())


if __name__ == "__main__":
    unittest.main()
