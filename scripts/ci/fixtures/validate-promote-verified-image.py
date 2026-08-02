#!/usr/bin/env python3
"""Failure-path fixtures for two-phase verified cross-registry promotion."""

from __future__ import annotations

import hashlib
import importlib.util
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "promote-verified-image.py"
SPEC = importlib.util.spec_from_file_location("promote_verified_image", SCRIPT)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"could not import {SCRIPT}")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

MANIFEST = b'{"schemaVersion":2,"mediaType":"application/vnd.oci.image.index.v1+json","manifests":[]}'
DIGEST = f"sha256:{hashlib.sha256(MANIFEST).hexdigest()}"
CANDIDATE = f"ghcr.io/honua-io/honua-server@{DIGEST}"
TAGS = [
    "ghcr.io/honua-io/honua-server:v1-amd64",
    "ghcr.io/honua-io/honua-server:latest-amd64",
    "docker.io/honuaio/honua-server:v1-amd64",
]


class FakeClient:
    def __init__(self, mismatch_repository: str | None = None) -> None:
        self.calls: list[tuple[str, str, str | None]] = []
        self.mismatch_repository = mismatch_repository

    def copy_all(self, source: str, destination: str) -> None:
        self.calls.append(("copy", source, destination))

    def raw_manifest(self, reference: str) -> bytes:
        self.calls.append(("inspect", reference, None))
        if self.mismatch_repository and reference.startswith(self.mismatch_repository):
            return b"mismatched manifest"
        return MANIFEST


success = FakeClient()
MODULE.promote_verified_image(CANDIDATE, TAGS, "boundary-candidate-aot-amd64-sha", success)
copy_destinations = [destination for operation, _, destination in success.calls if operation == "copy"]
staged = [destination for destination in copy_destinations if destination and ":boundary-candidate-" in destination]
public = [destination for destination in copy_destinations if destination in TAGS]
assert len(staged) == 2, staged
assert public == TAGS, public
first_public_call = next(index for index, call in enumerate(success.calls) if call[2] in TAGS)
assert all(call[2] not in TAGS for call in success.calls[:first_public_call])

failure = FakeClient("docker.io/honuaio/honua-server")
try:
    MODULE.promote_verified_image(CANDIDATE, TAGS, "boundary-candidate-aot-amd64-sha", failure)
except RuntimeError as exc:
    assert "staged digest mismatch" in str(exc)
else:
    raise AssertionError("a staged digest mismatch must fail promotion")
assert all(call[2] not in TAGS for call in failure.calls), failure.calls

print("Verified-image promotion fixtures passed.")
