"""GDAL/OGR helpers shared by the GDAL lanes.

GDAL is driven through its Python bindings (the same C API ``ogrinfo``/``gdalinfo``
use). Two things are captured around every client call:

* the URLs GDAL itself says it fetched (``CPL_DEBUG`` ``HTTP: Fetch(...)`` lines),
  so a check can assert that the client exercised the governed endpoint rather
  than answering from a cached or client-side path; and
* GDAL's own error messages, which carry the server's problem/exception body.

Credentials are passed the way a GDAL user passes them, through
``GDAL_HTTP_HEADER_FILE`` (GDAL 3.8.4's OAPIF path does not apply the newer
``GDAL_HTTP_HEADERS`` option); the curl cache is cleared between credential modes so a
response fetched with one credential can never answer a request made with another.
"""
from __future__ import annotations

import os
import re
import tempfile
from contextlib import contextmanager
from dataclasses import dataclass, field

from osgeo import gdal, ogr

gdal.UseExceptions()
ogr.UseExceptions()

URL_IN_DEBUG = re.compile(r"(?P<url>https?://[^\s)]+)")
REQUEST_MARKERS = ("Fetch(", "Start download for", "WFS: http", "Downloading", "GetFileSize(")


@dataclass
class Session:
    fetched: list[str] = field(default_factory=list)
    errors: list[str] = field(default_factory=list)

    def fetched_path(self, fragment: str) -> list[str]:
        return [value for value in self.fetched if fragment in value]


def _header_file(headers: dict[str, str] | None) -> str | None:
    if not headers:
        return None
    handle = tempfile.NamedTemporaryFile("w", delete=False, suffix=".headers", encoding="utf-8")
    with handle:
        handle.write("".join(f"{name}: {value}\n" for name, value in headers.items()))
    return handle.name


@contextmanager
def session(headers: dict[str, str] | None = None, **config: str):
    """Scope one client interaction: credentials, config options, captured log."""
    record = Session()

    def handler(error_class, _number, message):
        if any(marker in message for marker in REQUEST_MARKERS):
            match = URL_IN_DEBUG.search(message)
            if match and not message.startswith("GDAL: GDALClose"):
                record.fetched.append(match.group("url").rstrip(",;"))
        elif error_class >= gdal.CE_Warning:
            record.errors.append(message)

    gdal.VSICurlClearCache()
    header_file = _header_file(headers)
    options = {"CPL_DEBUG": "ON", "GDAL_HTTP_TIMEOUT": "60", "GDAL_HTTP_HEADER_FILE": header_file}
    options.update(config)
    previous = {name: gdal.GetConfigOption(name) for name in options}
    for name, value in options.items():
        gdal.SetConfigOption(name, value)
    gdal.PushErrorHandler(handler)
    try:
        yield record
    finally:
        gdal.PopErrorHandler()
        for name, value in previous.items():
            gdal.SetConfigOption(name, value)
        gdal.VSICurlClearCache()
        if header_file:
            os.unlink(header_file)


def open_failure(connection: str, headers: dict[str, str] | None = None, flags: int = gdal.OF_VECTOR,
                 open_options: list[str] | None = None) -> str | None:
    """Try to open a dataset; return GDAL's error text, or ``None`` when it opened."""
    with session(headers) as record:
        try:
            if connection.startswith("WCS:"):
                dataset = wcs_open(connection)
            else:
                dataset = gdal.OpenEx(connection, flags, open_options=open_options or [])
        except RuntimeError as error:
            return str(error) + (" | " + " | ".join(record.errors) if record.errors else "")
        if dataset is None:
            return " | ".join(record.errors) or "open returned None"
        dataset = None
        return None


def http_status_in(message: str | None) -> int | None:
    if not message:
        return None
    for pattern in (r"HTTP error code\s*:\s*(\d{3})", r'"status"\s*:\s*(\d{3})', r"\b(40[0-9]|50[0-9])\b"):
        match = re.search(pattern, message)
        if match:
            return int(match.group(1))
    return None


def wcs_open(connection: str):
    """Open a WCS dataset with a private, empty response cache.

    GDAL's WCS driver persists capabilities and descriptions under
    ``~/.gdal/wcs_cache`` by default, which would let a response fetched with one
    credential answer a later request made without it.
    """
    cache = tempfile.mkdtemp(prefix="wcs-cache-")
    return gdal.OpenEx(connection, gdal.OF_RASTER, open_options=[f"CACHE={cache}", "CLEAR_CACHE=YES"])
