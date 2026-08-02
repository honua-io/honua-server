#!/usr/bin/env python3
"""Replicate a verified OCI image digest before assigning public registry tags."""

from __future__ import annotations

import argparse
import hashlib
import re
import subprocess
import sys
from collections.abc import Iterable


DIGEST_PATTERN = re.compile(r"sha256:[0-9a-f]{64}$")
TAG_PATTERN = re.compile(r"[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$")


def _candidate_digest(reference: str) -> str:
    _, separator, digest = reference.rpartition("@")
    if not separator or not DIGEST_PATTERN.fullmatch(digest):
        raise ValueError(f"candidate must use a canonical sha256 digest: {reference!r}")
    return digest


def _repository(reference: str) -> str:
    last_slash = reference.rfind("/")
    last_colon = reference.rfind(":")
    if last_colon <= last_slash:
        raise ValueError(f"public destination must include an explicit tag: {reference!r}")
    return reference[:last_colon]


class SkopeoClient:
    """Authenticated registry copy/inspection operations."""

    def __init__(self, authfile: str) -> None:
        self._authfile = authfile

    def copy_all(self, source: str, destination: str) -> None:
        subprocess.run(
            [
                "skopeo",
                "copy",
                "--all",
                "--preserve-digests",
                "--authfile",
                self._authfile,
                f"docker://{source}",
                f"docker://{destination}",
            ],
            check=True,
        )

    def raw_manifest(self, reference: str) -> bytes:
        completed = subprocess.run(
            [
                "skopeo",
                "inspect",
                "--raw",
                "--authfile",
                self._authfile,
                f"docker://{reference}",
            ],
            check=True,
            stdout=subprocess.PIPE,
        )
        return completed.stdout


def promote_verified_image(
    candidate: str,
    public_tags: Iterable[str],
    staging_tag: str,
    client: SkopeoClient,
) -> None:
    """Stage and verify every target registry before creating any public tag."""

    expected_digest = _candidate_digest(candidate)
    if not TAG_PATTERN.fullmatch(staging_tag):
        raise ValueError(f"invalid staging tag: {staging_tag!r}")

    tags = list(dict.fromkeys(tag.strip() for tag in public_tags if tag.strip()))
    if not tags:
        raise ValueError("at least one public destination tag is required")

    repositories = list(dict.fromkeys(_repository(tag) for tag in tags))
    staged_by_repository: dict[str, str] = {}

    # Phase one copies every manifest and referenced blob, then proves each
    # target registry retained the verified top-level digest. A failure here
    # occurs before any public release alias is assigned.
    for repository in repositories:
        staged = f"{repository}:{staging_tag}"
        client.copy_all(candidate, staged)
        actual_digest = f"sha256:{hashlib.sha256(client.raw_manifest(staged)).hexdigest()}"
        if actual_digest != expected_digest:
            raise RuntimeError(
                f"staged digest mismatch for {repository}: expected {expected_digest}, got {actual_digest}"
            )
        staged_by_repository[repository] = staged

    # Phase two assigns public aliases only from a digest-verified copy in the
    # same target repository, so no cross-registry blob transfer is implicit.
    for tag in tags:
        repository = _repository(tag)
        client.copy_all(staged_by_repository[repository], tag)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate", required=True)
    parser.add_argument("--staging-tag", required=True)
    parser.add_argument("--authfile", required=True)
    args = parser.parse_args(argv)

    try:
        promote_verified_image(
            args.candidate,
            sys.stdin,
            args.staging_tag,
            SkopeoClient(args.authfile),
        )
    except (OSError, RuntimeError, ValueError, subprocess.CalledProcessError) as exc:
        print(f"verified image promotion failed: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
