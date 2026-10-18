"""Fail-closed classification shared by the existing STAC validation wrappers."""
from __future__ import annotations

import re
import socket
import ssl
import subprocess
from dataclasses import dataclass
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit

import pystac
from pystac.validation import GetSchemaError
from requests.exceptions import ConnectionError as RequestsConnectionError
from requests.exceptions import Timeout as RequestsTimeout
from urllib3.exceptions import (
    ConnectTimeoutError,
    MaxRetryError,
    NewConnectionError,
    ReadTimeoutError,
    SSLError,
)

_SCHEMA_HOSTS = frozenset({"schemas.stacspec.org", "stac-extensions.github.io"})
_ANSI = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")


def process_output_text(value: str | bytes | None) -> str:
    if value is None:
        return ""
    return value.decode("utf-8", errors="replace") if isinstance(value, bytes) else value


def _published_schema_uri(value: str) -> bool:
    try:
        uri = urlsplit(value)
        return (
            uri.scheme == "https" and uri.hostname in _SCHEMA_HOSTS
            and uri.port in (None, 443) and not uri.username and not uri.password
            and not uri.query and not uri.fragment and uri.path.endswith(".json")
        )
    except ValueError:
        return False


def _transport_failure(error: BaseException) -> bool:
    if isinstance(error, HTTPError):
        return error.code in (429, 500, 502, 503, 504) and _published_schema_uri(error.url)
    if isinstance(error, URLError):
        return isinstance(error.reason, (socket.gaierror, TimeoutError, ConnectionError, ssl.SSLError))
    if isinstance(error, MaxRetryError):
        return isinstance(error.reason, (NewConnectionError, ConnectTimeoutError, ReadTimeoutError, SSLError))
    return isinstance(error, (RequestsConnectionError, RequestsTimeout, NewConnectionError,
                              ConnectTimeoutError, ReadTimeoutError, SSLError))


def schema_fetch_unavailable(error: BaseException) -> bool:
    """Only a typed schema-fetch wrapper with a real transport cause may skip.

    PySTAC also wraps malformed schema JSON and programming errors in
    GetSchemaError. Neither the wrapper alone nor network words in its message
    establish environmental unavailability. Follow bounded explicit causes to
    support jsonschema's reference wrappers without hiding unrelated defects.
    """
    seen: set[int] = set()
    current: BaseException | None = error
    for _ in range(16):
        if current is None or id(current) in seen:
            return False
        seen.add(id(current))
        if isinstance(current, pystac.errors.STACValidationError):
            return False
        if isinstance(current, GetSchemaError):
            match = re.match(r"^Error when fetching schema (\S+): ", str(current))
            return bool(match and _published_schema_uri(match[1])
                        and current.__cause__ is not None
                        and _transport_failure(current.__cause__))
        current = current.__cause__
    return False


def validate_with_pystac(obj: pystac.STACObject) -> tuple[bool, str]:
    try:
        obj.validate()
        return True, "validated against the published STAC JSON Schemas"
    except Exception as error:
        if not schema_fetch_unavailable(error):
            raise
        return False, (
            "pystac schema validation unavailable - a published STAC schema host "
            f"could not be reached ({type(error).__name__}: {str(error)[:160]})"
        )


@dataclass(frozen=True)
class ValidatorOutcome:
    status: str
    findings: tuple[str, ...]
    reason: str


def classify_validator_result(result: subprocess.CompletedProcess[str], *, timed_out: bool = False) -> ValidatorOutcome:
    """Unstructured CLI diagnostics cannot authenticate a schema-only outage.

    Keep any unsuccessful external sweep failed. A mention of a remote schema
    host is not evidence that unrelated conformance findings may be discarded.
    PySTAC's separate typed retrieval path can establish unavailable safely.
    """
    output = _ANSI.sub("", f"{process_output_text(result.stdout)}\n{process_output_text(result.stderr)}")
    lines = [line.strip() for line in output.splitlines()]
    findings_list: list[str] = []
    warnings = False
    for line in lines:
        if line.startswith("Warnings:"):
            warnings = True
        elif line.startswith("Errors:"):
            warnings = False
            if line != "Errors: none":
                findings_list.append(line)
        elif line.startswith(("Error ", "Failed")) or (line.startswith("- ") and not warnings):
            findings_list.append(line)
    findings = tuple(findings_list)
    failed = timed_out or result.returncode != 0 or bool(findings) or "Traceback (most recent call last):" in output
    reason = "timeout" if timed_out else "validator failure" if failed else "validated"
    return ValidatorOutcome("fail" if failed else "pass", findings, reason)
