# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
GDAL/OGR interoperability: WFS 2.0 — format export.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from .conftest import EvidenceCollector, OgrResult


@pytest.mark.integration
@pytest.mark.gdal
class TestWfsExport:
    """Verify ogr2ogr can export WFS data to GeoJSON, GeoPackage, and CSV."""

    def test_export_geojson(
        self,
        wfs_dsn: str,
        ogr_run,
        wfs_layer_name: str,
        tmp_path: Path,
        evidence_collector: EvidenceCollector,
    ):
        """Export WFS data to a GeoJSON file."""
        out = tmp_path / "wfs_export.geojson"
        result: OgrResult = ogr_run(
            [
                "ogr2ogr", "-f", "GeoJSON",
                str(out), wfs_dsn, wfs_layer_name,
            ],
        )
        result.assert_success("WFS GeoJSON export failed")

        assert out.exists(), "GeoJSON output file not created"
        data = json.loads(out.read_text())
        assert data["type"] == "FeatureCollection"
        assert len(data["features"]) > 0
        evidence_collector.record(
            "test_export_geojson", "wfs", "export_geojson", "pass",
        )

    def test_export_gpkg(
        self,
        wfs_dsn: str,
        ogr_run,
        wfs_layer_name: str,
        tmp_path: Path,
        evidence_collector: EvidenceCollector,
    ):
        """Export WFS data to a GeoPackage file."""
        out = tmp_path / "wfs_export.gpkg"
        result: OgrResult = ogr_run(
            [
                "ogr2ogr", "-f", "GPKG",
                str(out), wfs_dsn, wfs_layer_name,
            ],
        )
        result.assert_success("WFS GeoPackage export failed")

        assert out.exists(), "GeoPackage output file not created"
        assert out.stat().st_size > 0, "GeoPackage file is empty"
        evidence_collector.record(
            "test_export_gpkg", "wfs", "export_gpkg", "pass",
        )

    def test_export_csv(
        self,
        wfs_dsn: str,
        ogr_run,
        wfs_layer_name: str,
        tmp_path: Path,
        evidence_collector: EvidenceCollector,
    ):
        """Export WFS data to CSV and verify header row."""
        out_dir = tmp_path / "wfs_csv_out"
        out_dir.mkdir()
        result: OgrResult = ogr_run(
            [
                "ogr2ogr", "-f", "CSV",
                str(out_dir), wfs_dsn, wfs_layer_name,
            ],
        )
        result.assert_success("WFS CSV export failed")

        csv_files = list(out_dir.glob("*.csv"))
        assert len(csv_files) > 0, "No CSV files created"
        header = csv_files[0].read_text().splitlines()[0].lower()
        assert "name" in header, f"Expected 'name' in CSV header: {header}"
        evidence_collector.record(
            "test_export_csv", "wfs", "export_csv", "pass",
        )
