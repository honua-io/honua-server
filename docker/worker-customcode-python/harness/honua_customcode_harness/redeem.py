"""Exchange the job attempt's single-use code for its job capability.

The provider passes only the code: container overrides are readable by anyone allowed to
describe jobs in the account. The server spends the code on the first exchange, so a copy
read later is worthless, and if someone else redeemed it first this job fails closed.
"""

from __future__ import annotations

import re
import time
from http import HTTPStatus
from collections.abc import Callable
from dataclasses import dataclass
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlparse
from urllib.request import HTTPRedirectHandler, Request, build_opener

CODE_HEADER = "X-Honua-Redemption-Code"
RETRYABLE_STATUS = frozenset({409, 429, 500, 502, 503, 504})
RETRY_WINDOW_SECONDS = 120
RETRY_PAUSE_SECONDS = 2
MAX_CAPABILITY_BYTES = 256


class RedemptionError(RuntimeError):
    """The capability could not be obtained; the job must not run."""


@dataclass(frozen=True)
class RedemptionTarget:
    """The serving origin and the server-assigned job attempt a code is redeemed for."""

    base_url: str
    job_id: str
    attempt: str


class _NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *_args):
        return None


def redeem_capability(target: RedemptionTarget, code: str, opener: Callable[..., object] | None = None) -> str:
    """Return the job capability, or raise :class:`RedemptionError`."""
    request = _build_request(target, code)
    open_url = opener or build_opener(_NoRedirect()).open
    deadline = time.monotonic() + RETRY_WINDOW_SECONDS
    while True:
        try:
            with open_url(request, timeout=30) as response:
                return _read_capability(response)
        except HTTPError as exc:
            exc.close()
            if exc.code not in RETRYABLE_STATUS or time.monotonic() >= deadline:
                raise RedemptionError(f"Job capability redemption refused (HTTP {exc.code}).") from None
        except (URLError, TimeoutError, ConnectionError):
            if time.monotonic() >= deadline:
                raise RedemptionError("Job capability redemption did not complete before its retry deadline.") from None
        time.sleep(RETRY_PAUSE_SECONDS)


def _build_request(target: RedemptionTarget, code: str) -> Request:
    base_url, job_id, attempt = target.base_url, target.job_id, target.attempt
    parsed = urlparse(base_url)
    if parsed.scheme != "https" and not (parsed.scheme == "http" and parsed.hostname in {"localhost", "127.0.0.1", "::1"}):
        raise RedemptionError("Job capability redemption requires HTTPS (HTTP is permitted only for loopback tests).")
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", job_id or "") or not (attempt or "").isdecimal() or int(attempt) <= 0:
        raise RedemptionError("Job capability redemption requires server-assigned HONUA_JOB_ID and HONUA_JOB_ATTEMPT.")
    if not re.fullmatch(r"[0-9a-f]{64}", code or ""):
        raise RedemptionError("HONUA_JOB_REDEMPTION_CODE is malformed.")
    url = f"{base_url.rstrip('/')}/api/geoprocessing/jobs/{quote(job_id)}/attempts/{attempt}/capability"
    return Request(url, data=b"", method="POST", headers={CODE_HEADER: code, "Content-Length": "0"})


def _read_capability(response) -> str:
    if response.status != HTTPStatus.OK:
        raise RedemptionError("Job capability redemption returned an unexpected status.")
    capability = response.read(MAX_CAPABILITY_BYTES + 1).decode("ascii", errors="replace").strip()
    if not re.fullmatch(r"[0-9a-f]{64}", capability):
        raise RedemptionError("Job capability redemption returned a malformed capability.")
    return capability
