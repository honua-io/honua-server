# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
External STAC API conformance proof using stac-utils/stac-api-validator.
"""

from __future__ import annotations

import json
import subprocess
import sys

import pytest

from .conftest import (
    CompatibilityEvidenceCollector,
    write_stac_api_validator_artifact,
)


VALIDATOR_GEOMETRY = {
    "type": "Polygon",
    "coordinates": [
        [
            [-122.5000, 37.7000],
            [-122.4400, 37.7000],
            [-122.4400, 37.7450],
            [-122.5000, 37.7450],
            [-122.5000, 37.7000],
        ]
    ],
}

VALIDATOR_CONFORMANCE_CLASSES = [
    "collections",
    "filter",
]


def _validator_reported_failure(result: subprocess.CompletedProcess[str]) -> bool:
    output = f"\n{result.stdout}\n{result.stderr}\n"
    return (
        result.returncode != 0
        or "\nFailed.\n" in output
        or "\nError " in output
        or "Traceback (most recent call last):" in output
    )


@pytest.mark.integration
@pytest.mark.stac
def test_stac_api_validator_conformance(
    stac_api_url: str,
    test_collection_id: str,
    evidence_collector: CompatibilityEvidenceCollector,
):
    """Run the pinned external validator against the live seeded STAC API."""
    command = [
        sys.executable,
        "-m",
        "stac_api_validator",
        "--root-url",
        stac_api_url,
        "--collection",
        test_collection_id,
        "--geometry",
        json.dumps(VALIDATOR_GEOMETRY, separators=(",", ":")),
        "--fields-nested-property",
        "properties.name",
        "--no-validate-pagination",
    ]
    for conformance_class in VALIDATOR_CONFORMANCE_CLASSES:
        command.extend(["--conformance", conformance_class])

    result = subprocess.run(
        command,
        check=False,
        capture_output=True,
        text=True,
        timeout=180,
    )
    status = "fail" if _validator_reported_failure(result) else "pass"
    artifact_path = write_stac_api_validator_artifact(
        command=command,
        root_url=stac_api_url,
        collection_id=test_collection_id,
        geometry=VALIDATOR_GEOMETRY,
        conformance_classes=VALIDATOR_CONFORMANCE_CLASSES,
        status=status,
        returncode=result.returncode,
        stdout=result.stdout,
        stderr=result.stderr,
    )

    evidence_collector.record(
        "test_stac_api_validator_conformance",
        "stac-api-validator validated Honua STAC collections and filter classes",
        status,
        detail=(
            f"exit_code={result.returncode} artifact={artifact_path.name} "
            f"classes={','.join(VALIDATOR_CONFORMANCE_CLASSES)} "
            "advertised_class_follow_ups=#956,#957"
        ),
    )

    assert status == "pass", (
        "stac-api-validator failed; see "
        f"{artifact_path} for stdout/stderr and exact command"
    )
