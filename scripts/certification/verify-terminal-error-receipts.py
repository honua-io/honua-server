#!/usr/bin/env python3
"""Fail closed when the shared SDK terminal-error fixture loses its 40-cell contract."""

from __future__ import annotations

import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
FIXTURE = ROOT / "docs" / "gis" / "data" / "terminal-error-receipts.v1.json"


def main() -> int:
    payload = json.loads(FIXTURE.read_text(encoding="utf-8"))
    paths = payload["sdkPaths"]
    failures = payload["failureClasses"]
    cells = {(path["id"], failure["id"]) for path in paths for failure in failures}

    assert payload["manifestId"] == "honua.terminal-error-receipts/v1"
    assert len(paths) == 8
    assert len(failures) == 5
    assert len(cells) == payload["expectedCellCount"] == 40
    assert {failure["id"] for failure in failures} == {
        "authz-denied",
        "not-found",
        "validation",
        "conflict",
        "backpressure",
    }
    assert failures[0]["authenticationRequired"]["httpStatus"] == 401
    assert failures[0]["httpStatus"] == 403
    assert payload["wireShapes"]["geoservices-http-200"]["transportStatus"] == 200
    assert payload["wireShapes"]["grpc"]["retainInitialMetadata"] is True
    assert payload["wireShapes"]["grpc"]["retainTrailingMetadata"] is True
    assert payload["safety"]["rawBodyInDefaultSerialization"] is False
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
