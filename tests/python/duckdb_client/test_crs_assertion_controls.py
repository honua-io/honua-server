# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
"""Negative controls for the canonical CRS assertions; these do not certify a server."""
from types import SimpleNamespace

import pytest

from shared import canonical_fixture as fx
from .test_ogc_features import (
    CRS84_URI,
    test_disc_02_single_collection_metadata as _metadata_probe,
    test_geom_02_crs_is_crs84 as _geometry_probe,
)

pytestmark = pytest.mark.duckdb_client


@pytest.fixture(scope="session", autouse=True)
def _write_cert_evidence():
    """Synthetic assertion controls must never emit live qualification evidence."""
    yield


class Observations:
    def __init__(self, *rows):
        self.rows = iter(rows)
        self.recorded = []

    def execute(self, _sql):
        return self

    def fetchone(self):
        return next(self.rows)

    def record(self, *args, **kwargs):
        self.recorded.append((args, kwargs))


def test_exact_published_crs_and_unswapped_anchor_are_accepted():
    metadata = Observations((fx.COLLECTION_ID, "feature", CRS84_URI, [-123, 37, -122, 38]))
    _metadata_probe(metadata, "http://controlled.invalid", metadata, SimpleNamespace(ms=0))
    geometry = Observations(("OGC", "CRS84"), ("OGC:CRS84", fx.ANCHOR_LON, fx.ANCHOR_LAT))
    _geometry_probe(geometry, "controlled", geometry, SimpleNamespace(ms=0))
    assert metadata.recorded and geometry.recorded


@pytest.mark.parametrize("crs", ["http://www.opengis.net/def/crs/EPSG/0/4326", "garbage/4326", None])
def test_wrong_collection_crs_cannot_emit_pass(crs):
    observed = Observations((fx.COLLECTION_ID, "feature", crs, [-123, 37, -122, 38]))
    with pytest.raises(AssertionError):
        _metadata_probe(observed, "http://controlled.invalid", observed, SimpleNamespace(ms=0))
    assert not observed.recorded


@pytest.mark.parametrize("authority,geometry", [
    (("EPSG", "4326"), ("OGC:CRS84", fx.ANCHOR_LON, fx.ANCHOR_LAT)),
    (("OGC", "CRS84"), ("EPSG:4326", fx.ANCHOR_LON, fx.ANCHOR_LAT)),
    (("OGC", "CRS84"), ("OGC:CRS84", fx.ANCHOR_LAT, fx.ANCHOR_LON)),
    (("OGC", "CRS84"), ("OGC:CRS84", fx.ANCHOR_LON + 1, fx.ANCHOR_LAT)),
    (("OGC", "CRS84"), ("OGC:CRS84", fx.ANCHOR_LON, fx.ANCHOR_LAT + 1)),
    (("OGC", "CRS84"), None),
])
def test_wrong_authority_typed_crs_swapped_or_missing_anchor_cannot_emit_pass(authority, geometry):
    observed = Observations(authority, geometry)
    with pytest.raises(AssertionError):
        _geometry_probe(observed, "controlled", observed, SimpleNamespace(ms=0))
    assert not observed.recorded
