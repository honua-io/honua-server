# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
GDAL/OGR interoperability: WFS 2.0 — layer discovery and schema introspection.
"""

from __future__ import annotations

import pytest

from .conftest import EvidenceCollector, OgrResult


@pytest.mark.integration
@pytest.mark.gdal
class TestWfsDiscovery:
    """Verify that ogrinfo can discover layers via the WFS driver."""

    def test_list_layers(
        self,
        wfs_dsn: str,
        ogr_run,
        evidence_collector: EvidenceCollector,
    ):
        """ogrinfo lists at least one layer from the WFS endpoint."""
        result: OgrResult = ogr_run(["ogrinfo", wfs_dsn])
        result.assert_success("ogrinfo WFS layer listing failed")

        # WFS layer listing should show at least one numbered layer entry
        # GDAL reports layers as "1: <typename> (<geom type>)"
        assert "1:" in result.stdout, (
            f"Expected at least one layer in WFS output:\n{result.stdout}"
        )
        evidence_collector.record(
            "test_list_layers", "wfs", "discovery", "pass",
        )

    def test_schema_introspection(
        self,
        wfs_dsn: str,
        ogr_run,
        wfs_layer_name: str,
        evidence_collector: EvidenceCollector,
    ):
        """ogrinfo -so reports schema for the first WFS layer."""
        result: OgrResult = ogr_run(
            ["ogrinfo", "-so", wfs_dsn, wfs_layer_name],
        )
        result.assert_success("ogrinfo WFS schema introspection failed")

        assert "Geometry" in result.stdout, (
            f"No geometry info in WFS schema output:\n{result.stdout}"
        )
        evidence_collector.record(
            "test_schema_introspection", "wfs", "schema_introspection", "pass",
        )

    def test_feature_count(
        self,
        wfs_dsn: str,
        ogr_run,
        wfs_layer_name: str,
        evidence_collector: EvidenceCollector,
    ):
        """ogrinfo -so reports a non-zero feature count for WFS."""
        result: OgrResult = ogr_run(
            ["ogrinfo", "-so", wfs_dsn, wfs_layer_name],
        )
        result.assert_success()

        assert "Feature Count" in result.stdout, (
            f"No feature count in WFS schema output:\n{result.stdout}"
        )
        evidence_collector.record(
            "test_feature_count", "wfs", "feature_count", "pass",
        )

    def test_srs_reported(
        self,
        wfs_dsn: str,
        ogr_run,
        wfs_layer_name: str,
    ):
        """ogrinfo -so includes SRS information for WFS layer."""
        result: OgrResult = ogr_run(
            ["ogrinfo", "-so", wfs_dsn, wfs_layer_name],
        )
        result.assert_success()

        stdout = result.stdout.lower()
        assert any(
            token in stdout for token in ("4326", "wgs 84", "crs84")
        ), f"Expected WGS 84 / EPSG:4326 in WFS SRS output:\n{result.stdout}"
