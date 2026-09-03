#!/usr/bin/env python3
"""Fail closed when the shared SDK terminal-error fixture loses its 40-cell contract."""

from __future__ import annotations

import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
FIXTURE = ROOT / "docs" / "gis" / "data" / "terminal-error-receipts.v1.json"

EXPECTED_FAILURE_IDS = {
    "authz-denied",
    "not-found",
    "validation",
    "conflict",
    "backpressure",
}
EXPECTED_RECEIPT_FIELDS = {
    "transportStatus",
    "protocolCode",
    "kind",
    "code",
    "retryable",
    "retryAfterSeconds",
    "correlationId",
    "fieldErrors",
    "protocolMetadata",
}
EXPECTED_SENSITIVE_METADATA_KEYS = {
    "authorization",
    "cookie",
    "set-cookie",
    "x-api-key",
}
EXPECTED_HTTP_BODY_FIELDS = {
    "type",
    "title",
    "status",
    "detail",
    "instance",
    "version",
    "kind",
    "code",
    "correlationId",
    "retryable",
    "retryAfterSeconds",
    "errors",
}
EXPECTED_GEOSERVICES_BODY_FIELDS = {
    "code",
    "message",
    "details",
    "retryable",
    "retryAfterSeconds",
}
EXPECTED_GRPC_CORRELATION_KEYS = {
    "x-correlation-id",
    "honua-request-id",
    "x-request-id",
    "honua-correlation-id",
}


def validate(payload: dict) -> None:
    """Validate the complete contract shape, raising on any drift."""

    paths = payload["sdkPaths"]
    failures = payload["failureClasses"]
    cells = {(path["id"], failure["id"]) for path in paths for failure in failures}
    wire_shapes = payload["wireShapes"]

    assert payload["manifestId"] == "honua.terminal-error-receipts/v1"
    assert len(paths) == 8
    assert len(failures) == 5
    assert len(cells) == payload["expectedCellCount"] == 40
    assert {failure["id"] for failure in failures} == EXPECTED_FAILURE_IDS
    assert failures[0]["authenticationRequired"]["httpStatus"] == 401
    assert failures[0]["httpStatus"] == 403
    assert wire_shapes["geoservices-http-200"]["transportStatus"] == 200

    assert set(wire_shapes["http"]["bodyFields"]) == EXPECTED_HTTP_BODY_FIELDS
    assert (
        set(wire_shapes["geoservices-http-200"]["bodyFields"])
        == EXPECTED_GEOSERVICES_BODY_FIELDS
    )
    grpc = wire_shapes["grpc"]
    assert set(grpc["correlationKeys"]) == EXPECTED_GRPC_CORRELATION_KEYS
    assert grpc["machineCodeKey"] == "honua-error-code"
    assert grpc["kindKey"] == "honua-error-kind"
    assert grpc["retryableKey"] == "honua-retryable"
    assert grpc["retryKey"] == "retry-after"
    assert grpc["structuredErrorsKey"] == "honua-error-details"
    assert grpc["retainInitialMetadata"] is True
    assert grpc["retainTrailingMetadata"] is True

    assert set(payload["receiptFields"]) == EXPECTED_RECEIPT_FIELDS
    assert set(payload["safety"]["sensitiveMetadataKeys"]) == EXPECTED_SENSITIVE_METADATA_KEYS
    assert payload["safety"]["rawBodyInDefaultSerialization"] is False


def main() -> int:
    payload = json.loads(FIXTURE.read_text(encoding="utf-8"))
    validate(payload)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
