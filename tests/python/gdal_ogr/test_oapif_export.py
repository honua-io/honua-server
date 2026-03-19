# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
GDAL/OGR interoperability: OGC API Features — format export.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from .conftest import EvidenceCollector, OgrResult


@pytest.mark.integration
@pytest.mark.gdal
class TestOapifExport:
    """Verify ogr2ogr can export OAPIF data to GeoJSON, GeoPackage, and CSV."""

    def test_export_geojson(
        self,
        oapif_dsn: str,
        ogr_run,
        test_collection_id: str,
        tmp_path: Path,
        evidence_collector: EvidenceCollector,
    ):
        """Export to a GeoJSON file and validate its structure."""
        out = tmp_path / "export.geojson"
        result: OgrResult = ogr_run(
            [
                "ogr2ogr", "-f", "GeoJSON",
                str(out), oapif_dsn, test_collection_id,
            ],
        )
        result.assert_success("GeoJSON export failed")

        assert out.exists(), "GeoJSON output file not created"
        data = json.loads(out.read_text())
        assert data["type"] == "FeatureCollection"
        assert len(data["features"]) > 0
        evidence_collector.record(
            "test_export_geojson", "oapif", "export_geojson", "pass",
        )

    def test_export_gpkg(
        self,
        oapif_dsn: str,
        ogr_run,
        test_collection_id: str,
        tmp_path: Path,
        evidence_collector: EvidenceCollector,
    ):
        """Export to a GeoPackage file."""
        out = tmp_path / "export.gpkg"
        result: OgrResult = ogr_run(
            [
                "ogr2ogr", "-f", "GPKG",
                str(out), oapif_dsn, test_collection_id,
            ],
        )
        result.assert_success("GeoPackage export failed")

        assert out.exists(), "GeoPackage output file not created"
        assert out.stat().st_size > 0, "GeoPackage file is empty"
        evidence_collector.record(
            "test_export_gpkg", "oapif", "export_gpkg", "pass",
        )

    def test_export_csv(
        self,
        oapif_dsn: str,
        ogr_run,
        test_collection_id: str,
        tmp_path: Path,
        evidence_collector: EvidenceCollector,
    ):
        """Export to CSV and verify header row is present."""
        out_dir = tmp_path / "csv_out"
        out_dir.mkdir()
        result: OgrResult = ogr_run(
            [
                "ogr2ogr", "-f", "CSV",
                str(out_dir), oapif_dsn, test_collection_id,
            ],
        )
        result.assert_success("CSV export failed")

        csv_files = list(out_dir.glob("*.csv"))
        assert len(csv_files) > 0, "No CSV files created"
        header = csv_files[0].read_text().splitlines()[0].lower()
        assert "name" in header, f"Expected 'name' in CSV header: {header}"
        evidence_collector.record(
            "test_export_csv", "oapif", "export_csv", "pass",
        )
