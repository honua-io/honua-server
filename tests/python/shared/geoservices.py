# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""Assertion helpers for the Esri GeoServices REST error contract.

PA-070 / PA-117 (honua-server #2418) aligned the GeoServices REST surface
(paths under ``/rest/services`` and ``/tiles``) with the Esri ArcGIS REST
convention: operation errors are signalled with **HTTP 200** and an
``{"error": {"code": N, ...}}`` JSON body rather than a non-2xx HTTP status.
Modern OGC API (RFC 7807) error paths were intentionally left untouched.

These helpers assert that contract while remaining tolerant of a legacy
non-2xx status, so they keep catching real regressions (a plain success body
where an error is expected) without re-encoding the pre-parity status codes.
"""

from __future__ import annotations

from typing import Container, Optional

import httpx


def assert_geoservices_error(
    response: httpx.Response,
    *,
    body_codes: Optional[Container[int]] = None,
    allow_empty: bool = False,
) -> None:
    """Assert a GeoServices REST/tiles error response.

    Accepts either the Esri 200 + ``{"error": {...}}`` body contract or a
    legacy ``>= 400`` status. When the response is 200 it MUST carry an
    ``error`` object, so a silent success body can never satisfy the
    assertion.

    Args:
        response: the HTTP response to inspect.
        body_codes: if provided, the ``error.code`` of a 200 body (or the HTTP
            status of a legacy ``>= 400`` response) must be one of these.
        allow_empty: also accept an empty ``204 No Content`` (used by tile
            endpoints that emit an empty tile rather than an error body).
    """
    status = response.status_code

    if allow_empty and status == 204:
        return

    if status >= 400:
        if body_codes is not None:
            assert status in body_codes, f"unexpected error status {status}"
        return

    assert status == 200, (
        f"expected 200 or >= 400, got {status}: {response.text[:300]}"
    )

    try:
        body = response.json()
    except ValueError as exc:  # pragma: no cover - defensive
        raise AssertionError(
            "expected a JSON GeoServices error body, got "
            f"{response.headers.get('content-type')}: {response.content[:200]!r}"
        ) from exc

    error = body.get("error") if isinstance(body, dict) else None
    assert isinstance(error, dict), (
        "expected a GeoServices error object {'error': {...}}, "
        f"got: {str(body)[:300]}"
    )
    if body_codes is not None:
        assert error.get("code") in body_codes, error
