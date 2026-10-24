# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""
GDAL/OGR interoperability: WFS 2.0 — format export.
"""

from __future__ import annotations

import csv
import json
from pathlib import Path

import httpx
import pytest

from .conftest import EvidenceCollector, OgrResult
from .query_oracles import exact_rows, identity, source_subset


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
        http_client: httpx.Client,
        wfs_dsn: str,
        ogr_run,
        wfs_layer_name: str,
        tmp_path: Path,
        evidence_collector: EvidenceCollector,
    ):
        """Export the qualified WFS type to a safe CSV filename and retain its rows."""
        baseline = http_client.get("/wfs", params={"SERVICE": "WFS", "REQUEST": "GetFeature", "VERSION": "2.0.0",
            "TYPENAMES": wfs_layer_name, "OUTPUTFORMAT": "application/geo+json", "COUNT": 100})
        assert baseline.status_code == 200, baseline.text
        source = baseline.json()
        source_subset(source, wfs_layer_name)
        expected = {identity(row, wfs_layer_name): {"name": row["properties"]["name"]} for row in source["features"]}
        out_dir = tmp_path / "wfs_csv_out"
        result: OgrResult = ogr_run(
            [
                "ogr2ogr", "-f", "CSV",
                str(out_dir), wfs_dsn, wfs_layer_name,
                "-nln", "wfs_export",
            ],
        )
        result.assert_success("WFS CSV export failed")

        csv_files = list(out_dir.glob("*.csv"))
        assert csv_files == [out_dir / "wfs_export.csv"], "expected the exact safe CSV output filename"
        with csv_files[0].open(newline="", encoding="utf-8") as stream:
            reader = csv.DictReader(stream)
            headers = [name.lower() for name in (reader.fieldnames or [])]
            assert len(headers) == len(set(headers)) and {"objectid", "name"} <= set(headers), "CSV identity/name headers are missing or ambiguous"
            features = [{"properties": {key.lower(): value for key, value in row.items()}} for row in reader]
        exact_rows({"type": "FeatureCollection", "features": features}, expected, wfs_layer_name)
        evidence_collector.record(
            "test_export_csv", "wfs", "export_csv", "pass",
        )
