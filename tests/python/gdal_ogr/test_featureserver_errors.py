# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""GDAL/OGR interoperability: GeoServices FeatureServer, error handling."""

from __future__ import annotations

import pytest

from .conftest import EvidenceCollector, esrijson_dsn
from .geoservices_helpers import geoservices_error_code


@pytest.mark.integration
@pytest.mark.gdal
class TestFeatureServerErrors:
    """A missing layer is a structured server error that GDAL surfaces, never an empty layer."""

    def test_missing_layer_fails_to_open(self, featureserver_layer_url, ogr_run, evidence_collector: EvidenceCollector):
        missing = featureserver_layer_url.rsplit("/", 1)[0] + "/987654"
        result = ogr_run(["ogrinfo", "-so", "-al", esrijson_dsn(missing)])

        assert not result.succeeded, f"GDAL opened a missing FeatureServer layer:\n{result.stdout}"
        assert geoservices_error_code(esrijson_dsn(missing)) is not None, \
            "the server did not answer the missing layer with a structured GeoServices error"
        evidence_collector.record("test_missing_layer_fails_to_open", "featureserver", "errors", "pass")
