# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""Certification evidence collectors and routing helpers for Day-1 PyQGIS protocols."""

from __future__ import annotations

from pathlib import Path
from typing import Any

import pytest

DAY1_PROTOCOL_VERSIONS = {
    "wms": "1.3.0",
    "wmts": "1.0.0",
    "mvt": "1.0",
    "featureserver": "1.0",
}

_extra_evidence: dict[str, Any] = {}


def get_or_create_extra_evidence(pyqgis_runtime, qgis_version: str, protocol: str):
    """Return or lazily construct a session collector for a Day-1 protocol."""
    from .conftest import CertificationEvidenceCollector

    collector = _extra_evidence.get(protocol)
    if collector is None:
        collector = CertificationEvidenceCollector(pyqgis_runtime, qgis_version, protocol)
        _extra_evidence[protocol] = collector
    return collector


def resolve_protocol_collector(item: pytest.Item):
    """Resolve an evidence collector from a ``@pytest.mark.cert_protocol`` marker."""
    marker = item.get_closest_marker("cert_protocol")
    if marker and marker.args:
        return _extra_evidence.get(str(marker.args[0]))
    return None


def write_extra_envelopes(results_dir: Path, run_id: str, suffix: str) -> None:
    """Persist ``.cert.json`` envelopes for any Day-1 collectors with records."""
    for protocol, collector in sorted(_extra_evidence.items()):
        if collector.has_records:
            path = results_dir / f"{run_id}-desktop-qgis-{protocol}{suffix}.cert.json"
            collector.write_envelope(path)


@pytest.fixture(scope="session")
def wms_evidence(pyqgis_runtime, qgis_version: str):
    """Session-scoped WMS certification evidence collector."""
    return get_or_create_extra_evidence(pyqgis_runtime, qgis_version, "wms")


@pytest.fixture(scope="session")
def wmts_evidence(pyqgis_runtime, qgis_version: str):
    """Session-scoped WMTS certification evidence collector."""
    return get_or_create_extra_evidence(pyqgis_runtime, qgis_version, "wmts")


@pytest.fixture(scope="session")
def mvt_evidence(pyqgis_runtime, qgis_version: str):
    """Session-scoped MVT / VectorTileServer certification evidence collector."""
    return get_or_create_extra_evidence(pyqgis_runtime, qgis_version, "mvt")


@pytest.fixture(scope="session")
def featureserver_evidence(pyqgis_runtime, qgis_version: str):
    """Session-scoped GeoServices FeatureServer certification evidence collector."""
    return get_or_create_extra_evidence(pyqgis_runtime, qgis_version, "featureserver")
