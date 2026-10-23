"""Headless PyQGIS helpers for the ``desktop-qgis`` lane.

QGIS is driven the way a desktop user drives it: data-source URIs for the OAPIF,
WFS, WMS/WMTS, ArcGIS and vector-tile providers, the GDAL provider for OGC API
Maps/Tiles rasters, and credentials stored in the QGIS authentication database as
``APIHeader`` configurations referenced by ``authcfg``.

Every reply QGIS's network access manager completes is recorded (method, URL,
HTTP status, content type), so a check can assert which governed endpoint the
client requested and what the server answered. GDAL-provider traffic does not go
through QGIS's network manager; it is captured through GDAL's own debug log.
"""
from __future__ import annotations

import os
import re
import tempfile
from contextlib import contextmanager
from dataclasses import dataclass, field

from qgis.core import (
    QgsApplication,
    QgsAuthMethodConfig,
    QgsCoordinateReferenceSystem,
    QgsMapRendererParallelJob,
    QgsMapSettings,
    QgsNetworkAccessManager,
    QgsNetworkRequestParameters,
    QgsRectangle,
)
from qgis.PyQt.QtCore import QSize, QEventLoop, QTimer
from qgis.PyQt.QtGui import QColor
from qgis.PyQt.QtNetwork import QNetworkAccessManager, QNetworkRequest

from rosterenv import EXPIRED_BEARER, WRONG_API_KEY, api_key_headers, bearer_headers

# Every QGIS process gets its own authentication database: a child process that
# inherited a parent's path would find a database locked by another master password.
os.environ["QGIS_AUTH_DB_DIR_PATH"] = tempfile.mkdtemp(prefix="qgis-auth-")

APP = QgsApplication([], False)
APP.initQgis()

_OPERATIONS = {
    QNetworkAccessManager.HeadOperation: "HEAD", QNetworkAccessManager.GetOperation: "GET",
    QNetworkAccessManager.PutOperation: "PUT", QNetworkAccessManager.PostOperation: "POST",
    QNetworkAccessManager.DeleteOperation: "DELETE", QNetworkAccessManager.CustomOperation: "CUSTOM",
}


@dataclass
class Exchange:
    method: str
    url: str
    status: int | None
    content_type: str | None


@dataclass
class Recorder:
    exchanges: list[Exchange] = field(default_factory=list)
    gdal_fetched: list[str] = field(default_factory=list)

    def matching(self, fragment: str, method: str | None = None) -> list[Exchange]:
        pump()
        return [item for item in self.exchanges if fragment in item.url and (method is None or item.method == method)]

    def statuses(self, fragment: str) -> list[int | None]:
        return [item.status for item in self.matching(fragment)]

    def summary(self, limit: int = 12) -> list[str]:
        pump()
        return [f"{item.method} {item.url} -> {item.status} {item.content_type or ''}".strip()
                for item in self.exchanges[:limit]]


_ACTIVE: list[Recorder] = []
_METHODS: dict[int, str] = {}


def _on_request(parameters) -> None:
    _METHODS[parameters.requestId()] = _OPERATIONS.get(parameters.operation(), "CUSTOM")


def _on_finished(reply) -> None:
    content_type = bytes(reply.rawHeader(b"Content-Type")).decode("latin-1") or None
    exchange = Exchange(
        method=_METHODS.pop(reply.requestId(), "?"),
        url=reply.request().url().toString(),
        status=reply.attribute(QNetworkRequest.HttpStatusCodeAttribute),
        content_type=content_type,
    )
    for recorder in _ACTIVE:
        recorder.exchanges.append(exchange)


QgsNetworkAccessManager.instance().requestAboutToBeCreated[QgsNetworkRequestParameters].connect(_on_request)
QgsNetworkAccessManager.instance().finished.connect(_on_finished)


def pump(milliseconds: int = 50) -> None:
    """Deliver queued cross-thread network notifications to the main thread."""
    loop = QEventLoop()
    QTimer.singleShot(milliseconds, loop.quit)
    loop.exec_()
    APP.processEvents()


@contextmanager
def recording():
    from osgeo import gdal

    recorder = Recorder()
    _ACTIVE.append(recorder)
    cache = QgsNetworkAccessManager.instance().cache()
    if cache is not None:
        cache.clear()
    previous_debug = gdal.GetConfigOption("CPL_DEBUG")
    gdal.SetConfigOption("CPL_DEBUG", "HTTP")

    def handler(_class, _number, message):
        if "Fetch(" in message or "Start download for" in message:
            match = re.search(r"https?://[^\s)]+", message)
            if match:
                recorder.gdal_fetched.append(match.group(0))

    gdal.PushErrorHandler(handler)
    try:
        yield recorder
    finally:
        pump()
        gdal.PopErrorHandler()
        gdal.SetConfigOption("CPL_DEBUG", previous_debug)
        gdal.VSICurlClearCache()
        _ACTIVE.remove(recorder)


def _store(name: str, headers: dict[str, str]) -> str:
    manager = QgsApplication.authManager()
    config = QgsAuthMethodConfig("APIHeader")
    config.setName(name)
    for header, value in headers.items():
        config.setConfig(header, value)
    stored = manager.storeAuthenticationConfig(config)
    ok = stored[0] if isinstance(stored, tuple) else stored
    if not ok:
        raise RuntimeError(f"could not store QGIS auth configuration {name}")
    return config.id()


_manager = QgsApplication.authManager()
if not _manager.setMasterPassword("roster-local-authdb-" + os.urandom(8).hex(), True):
    raise RuntimeError("could not initialise the QGIS authentication database")

AUTHCFG = {
    "api-key": _store("roster api key", api_key_headers()),
    "oidc-bearer": _store("roster oidc bearer", bearer_headers()),
    "wrong-api-key": _store("roster wrong api key", WRONG_API_KEY),
    "expired-bearer": _store("roster expired bearer", EXPIRED_BEARER),
}


def in_fresh_process(module: str, function: str, *arguments) -> object:
    """Run ``module.function(*arguments)`` in a new QGIS process and return its JSON result.

    QGIS caches service capabilities in-process keyed by URL, not by credential, so
    comparing credential modes in one process would let one mode's capabilities
    answer another's. Each mode therefore gets its own process.
    """
    import json
    import subprocess
    import sys

    code = (f"import json, {module} as m; "
            f"print('ROSTER-RESULT ' + json.dumps(m.{function}(*{list(arguments)!r})))")
    environment = {key: value for key, value in os.environ.items() if key != "QGIS_AUTH_DB_DIR_PATH"}
    completed = subprocess.run([sys.executable, "-c", code], capture_output=True, text=True, check=False,
                               env=environment)
    for line in completed.stdout.splitlines():
        if line.startswith("ROSTER-RESULT "):
            return json.loads(line[len("ROSTER-RESULT "):])
    raise RuntimeError(f"{module}.{function}{arguments} produced no result (exit {completed.returncode}): "
                       f"{completed.stderr[-400:]}")


def render_box(layer, extent: QgsRectangle, crs: str, size: int = 256) -> tuple[int, tuple[int, int, int, int] | None]:
    """Render one layer and return (painted pixels, bounding box of painted pixels)."""
    settings = QgsMapSettings()
    settings.setLayers([layer])
    settings.setDestinationCrs(QgsCoordinateReferenceSystem(crs))
    settings.setExtent(extent)
    settings.setOutputSize(QSize(size, size))
    settings.setBackgroundColor(QColor(255, 255, 255, 0))
    job = QgsMapRendererParallelJob(settings)
    job.start()
    job.waitForFinished()
    image = job.renderedImage()
    xs, ys = [], []
    for y in range(image.height()):
        for x in range(image.width()):
            if (image.pixel(x, y) >> 24) & 0xFF:
                xs.append(x)
                ys.append(y)
    return len(xs), ((min(xs), min(ys), max(xs), max(ys)) if xs else None)


def render(layer, extent: QgsRectangle, crs: str, size: int = 256) -> tuple[int, int]:
    """Render one layer and return (non-background pixels, distinct colours)."""
    settings = QgsMapSettings()
    settings.setLayers([layer])
    settings.setDestinationCrs(QgsCoordinateReferenceSystem(crs))
    settings.setExtent(extent)
    settings.setOutputSize(QSize(size, size))
    settings.setBackgroundColor(QColor(255, 255, 255, 0))
    job = QgsMapRendererParallelJob(settings)
    job.start()
    job.waitForFinished()
    image = job.renderedImage()
    painted, colours = 0, set()
    for y in range(0, image.height(), 2):
        for x in range(0, image.width(), 2):
            pixel = image.pixel(x, y)
            if (pixel >> 24) & 0xFF:
                painted += 1
                colours.add(pixel)
    return painted, len(colours)
