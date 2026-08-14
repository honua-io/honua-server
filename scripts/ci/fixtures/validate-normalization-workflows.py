#!/usr/bin/env python3
"""Static security contract for the normalization producer/consumer workflows."""

from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
PRODUCER = ROOT / ".github/workflows/normalize-derived-artifacts.yml"
CONSUMER = ROOT / ".github/workflows/normalize-derived-artifacts-consumer.yml"


def require(source: str, needle: str, message: str) -> None:
    if needle not in source:
        raise AssertionError(message)


def main() -> None:
    producer = PRODUCER.read_text(encoding="utf-8")
    consumer = CONSUMER.read_text(encoding="utf-8")

    require(producer, "name: Derived Artifact Normalization", "producer identity changed")
    require(producer, "  pull_request:\n", "producer must use an unprivileged pull_request event")
    require(producer, "  contents: read", "producer contents permission must be read-only")
    require(producer, "  packages: read", "producer package permission must be read-only")
    require(producer, "scripts/ci/normalization-envelope.py build", "producer must build the envelope")
    require(producer, "actions/upload-artifact@v7", "producer must upload one data artifact")
    if re.search(r"^\s+(?:actions|contents|pull-requests|statuses):\s+write\s*$", producer, re.MULTILINE):
        raise AssertionError("untrusted producer gained a write permission")
    if "secrets." in producer or "pull_request_target" in producer:
        raise AssertionError("untrusted producer must receive no secret or trusted event")

    require(consumer, 'workflows: ["Derived Artifact Normalization"]', "consumer workflow_run identity changed")
    require(consumer, "  NORMALIZATION_MODE: observe", "normalization must remain in observe mode")
    require(consumer, "  actions: read", "consumer needs bounded artifact read permission")
    require(consumer, "  contents: read", "observe consumer must not have contents: write")
    require(consumer, "  statuses: write", "consumer must publish the observation status")
    require(consumer, "ref: ${{ github.event.repository.default_branch }}", "consumer must check out default policy")
    require(consumer, "persist-credentials: false", "trusted checkout must not persist credentials")
    require(consumer, "scripts/ci/normalization-envelope.py validate-archive", "consumer must use trusted validation")
    require(consumer, "github.rest.git.getBlob", "consumer must compare Git blobs without PR checkout")
    if "actions/download-artifact" in consumer:
        raise AssertionError("trusted consumer must inspect the zip before any extraction")
    if "secrets." in consumer or "contents: write" in consumer:
        raise AssertionError("observe consumer must have no write secret or contents mutation permission")
    if "github.event.pull_request.head" in consumer or "github.head_ref" in consumer:
        raise AssertionError("workflow_run consumer must not check out the PR head")
    if "git push" in consumer or "git apply" in consumer:
        raise AssertionError("observe consumer must not mutate a branch")

    print("normalization-workflows=ok mode=observe")


if __name__ == "__main__":
    main()
