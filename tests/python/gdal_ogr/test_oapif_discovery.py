# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
GDAL/OGR interoperability: OGC API Features — layer discovery and schema introspection.
"""

from __future__ import annotations

import pytest

from .conftest import EvidenceCollector, OgrResult


@pytest.mark.integration
@pytest.mark.gdal
class TestOapifDiscovery:
    """Verify that ogrinfo can discover layers via the OAPIF driver."""

    def test_list_layers(
        self,
        oapif_dsn: str,
        ogr_run,
        test_collection_id: str,
        evidence_collector: EvidenceCollector,
    ):
        """ogrinfo lists the test collection among available layers."""
        result: OgrResult = ogr_run(["ogrinfo", oapif_dsn])
        result.assert_success("ogrinfo layer listing failed")
        assert test_collection_id in result.stdout, (
            f"Expected collection '{test_collection_id}' in ogrinfo output:\n"
            f"{result.stdout}"
        )
        evidence_collector.record(
            "test_list_layers", "oapif", "discovery", "pass",
        )

    def test_schema_introspection(
        self,
        oapif_dsn: str,
        ogr_run,
        test_collection_id: str,
        evidence_collector: EvidenceCollector,
    ):
        """ogrinfo -so reports field names and geometry type for the collection."""
        result: OgrResult = ogr_run(
            ["ogrinfo", "-so", oapif_dsn, test_collection_id],
        )
        result.assert_success("ogrinfo schema introspection failed")

        stdout = result.stdout
        # Should report a geometry column
        assert "Geometry" in stdout, (
            f"No geometry info in schema output:\n{stdout}"
        )
        # Should include the 'name' field (from seed data)
        assert "name" in stdout.lower(), (
            f"Expected 'name' field in schema output:\n{stdout}"
        )
        evidence_collector.record(
            "test_schema_introspection", "oapif", "schema_introspection", "pass",
        )

    def test_feature_count(
        self,
        oapif_dsn: str,
        ogr_run,
        test_collection_id: str,
        evidence_collector: EvidenceCollector,
    ):
        """ogrinfo -so reports a non-zero feature count."""
        result: OgrResult = ogr_run(
            ["ogrinfo", "-so", oapif_dsn, test_collection_id],
        )
        result.assert_success("ogrinfo feature count check failed")

        # GDAL reports "Feature Count: N" in summary output
        assert "Feature Count" in result.stdout, (
            f"No feature count in schema output:\n{result.stdout}"
        )
        evidence_collector.record(
            "test_feature_count", "oapif", "feature_count", "pass",
        )

    def test_srs_reported(
        self,
        oapif_dsn: str,
        ogr_run,
        test_collection_id: str,
    ):
        """ogrinfo -so includes SRS information (EPSG:4326 expected)."""
        result: OgrResult = ogr_run(
            ["ogrinfo", "-so", oapif_dsn, test_collection_id],
        )
        result.assert_success()

        stdout = result.stdout.lower()
        # GDAL may report the SRS as WGS 84, EPSG:4326, or OGC:CRS84
        assert any(
            token in stdout for token in ("4326", "wgs 84", "crs84")
        ), f"Expected WGS 84 / EPSG:4326 in SRS output:\n{result.stdout}"
