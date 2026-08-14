#!/usr/bin/env python3
"""Offline trust-boundary tests for normalization-envelope.py."""

from __future__ import annotations

import base64
import copy
import importlib.util
import json
import stat
import tempfile
import unittest
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
        },
    }


def raw(value: dict) -> bytes:
    return (json.dumps(value, sort_keys=True) + "\n").encode()


def expected() -> dict:
    return {
        "repository": REPOSITORY,
        "pull_request": PR,
        "source_sha": HEAD,
        "base_sha": BASE,
        "run_id": RUN_ID,
        "run_attempt": ATTEMPT,
    }


class EnvelopeTests(unittest.TestCase):
    def test_valid_envelope_round_trips(self) -> None:
        plan = MODULE.validate_envelope(raw(envelope()), **expected())
        self.assertEqual(set(MODULE.OUTPUT_LIMITS), {item["path"] for item in plan["outputs"]})

    def test_source_and_producer_identity_are_exact(self) -> None:
        value = envelope()
        value["source"]["sha"] = "d" * 40
        with self.assertRaisesRegex(MODULE.EnvelopeError, "source identity"):
            MODULE.validate_envelope(raw(value), **expected())

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
        value = envelope()
        value["generators"][0]["path"] = "scripts/unsafe.sh"
        with self.assertRaisesRegex(MODULE.EnvelopeError, "allowlist/order"):
            MODULE.validate_envelope(raw(value), **expected())

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
            built = MODULE.build_envelope(root, REPOSITORY, PR, HEAD, BASE, RUN_ID, ATTEMPT)
            validated = MODULE.validate_envelope(raw(built), **expected())
            self.assertEqual(3, len(validated["outputs"]))


if __name__ == "__main__":
    unittest.main()
