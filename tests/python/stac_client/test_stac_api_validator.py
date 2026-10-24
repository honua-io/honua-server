# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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


from .validation_outcomes import classify_validator_result, process_output_text


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

# core / features / item-search are validated end-to-end (re-enabled in honua-server#1925
# once Items always satisfy the STAC datetime invariant, which previously broke pystac
# hydration under Features and Item Search). The Item Search Filter Extension class ("filter")
# is now requested as well (honua-server#1932): the landing page and each collection advertise
# the OGC queryables rel link
# (http://www.opengis.net/def/rel/ogc/1.0/queryables) whose href matches the queryables
# document's $id, and the queryables documents pin JSON Schema draft 2019-09 as the validator
# requires. A CQL2 filter referencing an undeclared queryable returns a structured 400, which
# is the spec-permitted behavior given the queryables documents declare
# "additionalProperties": false. The OGC API Features filter class remains covered by its own
# conformance run elsewhere.
VALIDATOR_CONFORMANCE_CLASSES = [
    "core",
    "collections",
    "features",
    "item-search",
    "filter",
]
VALIDATOR_TIMEOUT_SECONDS = 90
VALIDATOR_TIMEOUT_EXIT_CODE = 124


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

    try:
        result = subprocess.run(
            command,
            check=False,
            capture_output=True,
            text=True,
            timeout=VALIDATOR_TIMEOUT_SECONDS,
        )
        status = classify_validator_result(result).status
        returncode = result.returncode
        stdout = result.stdout
        stderr = result.stderr
    except subprocess.TimeoutExpired as exc:
        status = "fail"
        returncode = VALIDATOR_TIMEOUT_EXIT_CODE
        stdout = process_output_text(exc.stdout or exc.output)
        timeout_message = (
            f"stac-api-validator timed out after {VALIDATOR_TIMEOUT_SECONDS} seconds"
        )
        stderr = "\n".join(
            part for part in [process_output_text(exc.stderr), timeout_message] if part
        )

    artifact_path = write_stac_api_validator_artifact(
        command=command,
        root_url=stac_api_url,
        collection_id=test_collection_id,
        geometry=VALIDATOR_GEOMETRY,
        conformance_classes=VALIDATOR_CONFORMANCE_CLASSES,
        status=status,
        returncode=returncode,
        stdout=stdout,
        stderr=stderr,
    )

    if status != "pass":
        pytest.fail(
            "stac-api-validator failed; see "
            f"{artifact_path} for stdout/stderr and exact command",
            pytrace=False,
        )

    evidence_collector.record(
        "test_stac_api_validator_conformance",
        "stac-api-validator validated Honua STAC core/collections/features/item-search classes",
        "pass",
        detail=(
            f"exit_code={returncode} artifact={artifact_path.name} "
            f"classes={','.join(VALIDATOR_CONFORMANCE_CLASSES)}"
        ),
    )
