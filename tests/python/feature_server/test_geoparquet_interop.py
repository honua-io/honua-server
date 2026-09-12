# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Server↔SDK GeoParquet interop conformance lane (honua-server#2845, part of #2842).

This is the *real-reader* half of the interop lane: it drives the live server's
GeoServices ``f=parquet`` output and round-trips it through the same standard
GeoParquet stack a serious SDK integration relies on — ``pyarrow`` (Parquet +
``geo`` metadata), ``geopandas`` (WKB decode + CRS reconstruction), ``pyproj``
(authoritative PROJJSON CRS), and ``shapely`` (geometry validity).

It proves the bytes the server emits actually interoperate with an independent
reader — CRS resolves, coordinates preserve in (x, y) axis order, attributes and
geometries round-trip — rather than only round-tripping through the server's own
encoder. The complementary in-process schema-conformance assertions (validating
the ``geo`` metadata against the authoritative GeoParquet 1.1.0 / PROJJSON v0.7
JSON Schemas) live in the .NET lane:
``tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/FeatureServer/Services/GeoParquetSdkInteropConformanceTests.cs``.

Reader-side SDK tracking: honua-sdk-js#630.
"""

from __future__ import annotations

import io
import json
import math

import geopandas as gpd
import httpx
import pyarrow.parquet as pyarrow_parquet
import pyproj
import pytest

# Required evidence runs on glibc Linux (CI ubuntu-latest). The native writer's
# musl runtime is unsupported by this lane; a 404/501 never declares an exemption.
# All reader dependencies are mandatory, including during local collection.
PARQUET_CONTENT_TYPE = "application/vnd.apache.parquet"


def _fetch_parquet(
    http_client: httpx.Client,
    service_id: str,
    layer_id: int,
    *,
    out_sr: int | None = None,
) -> httpx.Response:
    """Request GeoParquet from the FeatureServer query endpoint."""
    params: dict[str, object] = {"where": "1=1", "f": "parquet"}
    if out_sr is not None:
        params["outSR"] = out_sr
    return http_client.get(
        f"/rest/services/{service_id}/FeatureServer/{layer_id}/query",
        params=params,
    )


def _require_parquet_available(response: httpx.Response) -> None:
    """The supported interop lane must actually serve GeoParquet."""
    assert response.status_code == 200, (
        f"GeoParquet is required on the supported glibc runtime: HTTP "
        f"{response.status_code}: {response.text[:300] if response.status_code != 200 else ''}"
    )


def _read_geo_metadata(payload: bytes) -> dict:
    """Read the raw GeoParquet ``geo`` metadata via pyarrow (no geopandas decode)."""
    table = pyarrow_parquet.read_table(io.BytesIO(payload))
    metadata = table.schema.metadata or {}
    assert b"geo" in metadata, "Parquet file is missing the GeoParquet 'geo' metadata key"
    return json.loads(metadata[b"geo"].decode("utf-8"))


@pytest.mark.integration
@pytest.mark.featureserver
class TestGeoParquetSdkInterop:
    """Live server GeoParquet output round-tripped through the real geopandas reader."""

    def test_parquet_geo_metadata_matches_sdk_contract(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """The emitted ``geo`` metadata carries every field the SDK reader relies on."""
        response = _fetch_parquet(http_client, test_service_id, test_layer_id)
        _require_parquet_available(response)

        assert response.status_code == 200, response.text[:300]
        assert PARQUET_CONTENT_TYPE in response.headers.get("content-type", "").lower()
        # Parquet magic bytes.
        assert response.content[:4] == b"PAR1"

        geo = _read_geo_metadata(response.content)

        assert geo["version"] == "1.1.0"
        primary = geo["primary_column"]
        assert primary, "primary_column must be set"
        column = geo["columns"][primary]

        # Documented constraints: encoding is WKB and geometry_types are XY/XYZ only (no M/measured).
        assert column["encoding"] == "WKB"
        assert isinstance(column["geometry_types"], list)
        for geometry_type in column["geometry_types"]:
            assert " M" not in geometry_type and not geometry_type.endswith("M"), (
                f"M-measured geometry type leaked into GeoParquet output: {geometry_type}"
            )

        # GeoParquet 1.1 spatial-pruning covering: each ordinate maps onto the bbox struct column.
        covering_bbox = column["covering"]["bbox"]
        for ordinate in ("xmin", "ymin", "xmax", "ymax"):
            path = covering_bbox[ordinate]
            assert isinstance(path, list) and len(path) == 2 and path[1] == ordinate, (
                f"covering.bbox.{ordinate} must be a [column, '{ordinate}'] path, got {path!r}"
            )

    def test_parquet_geometry_and_attributes_round_trip_through_geopandas(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """geopandas decodes the WKB + CRS and matches the GeoJSON reference view."""
        parquet_response = _fetch_parquet(http_client, test_service_id, test_layer_id)
        _require_parquet_available(parquet_response)
        assert parquet_response.status_code == 200, parquet_response.text[:300]

        gdf = gpd.read_parquet(io.BytesIO(parquet_response.content))
        assert len(gdf) > 0, "GeoParquet round-trip produced no rows"

        # Default output CRS is EPSG:4326 (OGC:CRS84 lon/lat), resolved by pyproj from the geo metadata.
        assert gdf.crs is not None, "geopandas could not reconstruct the CRS from the geo metadata"
        assert gdf.crs.equals(pyproj.CRS.from_epsg(4326), ignore_axis_order=True)

        # Reference view: the same query as GeoJSON. Feature counts and names must agree.
        geojson_response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "1=1", "f": "geojson"},
        )
        assert geojson_response.status_code == 200
        reference = geojson_response.json()
        assert len(gdf) == len(reference["features"]), (
            "GeoParquet row count must match the GeoJSON feature count"
        )

        reference_names = sorted(
            f["properties"].get("name") for f in reference["features"]
        )
        assert sorted(gdf["name"].tolist()) == reference_names, "attribute 'name' did not round-trip"

        # Every decoded non-null geometry must be valid shapely geometry.
        non_null = gdf[gdf.geometry.notna()]
        assert len(non_null) > 0, "expected at least one non-null geometry"
        assert bool(non_null.geometry.is_valid.all()), "geopandas decoded an invalid geometry from WKB"

    def test_parquet_non_4326_output_preserves_crs_and_xy_axis_order(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Projected (EPSG:3857) output carries authoritative PROJJSON and (x, y) coordinates."""
        response = _fetch_parquet(http_client, test_service_id, test_layer_id, out_sr=3857)
        _require_parquet_available(response)
        # honua-server#4396: this used to skip on any non-200, which turned "outSR was
        # ignored" and "reprojection crashed" into a green cell. Reprojected GeoParquet is
        # part of the GA promise, so a non-200 fails.
        assert response.status_code == 200, (
            f"projected GeoParquet (outSR=3857) must be served; got HTTP "
            f"{response.status_code}: {response.text[:300]}"
        )

        # Raw geo metadata carries a PROJJSON crs object; pyproj must reconstruct EPSG:3857.
        geo = _read_geo_metadata(response.content)
        crs_projjson = geo["columns"][geo["primary_column"]]["crs"]
        assert isinstance(crs_projjson, dict), "non-4326 output must carry a PROJJSON crs object"
        assert pyproj.CRS.from_json_dict(crs_projjson).to_epsg() == 3857

        gdf = gpd.read_parquet(io.BytesIO(response.content))
        assert gdf.crs is not None and gdf.crs.to_epsg() == 3857

        # GeoParquet stores coordinates in (x, y) order regardless of the CRS axis order, so the
        # Web Mercator easting/northing land in metre range (|coord| far beyond lon/lat degrees).
        non_null = gdf[gdf.geometry.notna()]
        assert len(non_null) > 0
        minx, miny, maxx, maxy = non_null.total_bounds
        assert max(abs(minx), abs(maxx)) > 180.0, (
            "projected easting (X) must be in metres, proving reprojection + (x, y) axis order"
        )
        assert max(abs(miny), abs(maxy)) > 180.0, (
            "projected northing (Y) must be in metres, proving reprojection + (x, y) axis order"
        )

    @pytest.mark.parametrize("out_sr", [4326, 3857])
    def test_parquet_known_rows_preserve_values_geometry_nulls_and_covering(
        self, postgis, worker_schema, http_client, test_service_id, test_layer_id, out_sr
    ):
        """Decode a multi-row fixture against arithmetic, including a null final row."""
        row_count = 1025
        with postgis.get_connection(worker_schema) as connection:
            connection.execute("TRUNCATE features RESTART IDENTITY")
            connection.execute(
                """
                INSERT INTO features (layer_id, geometry, attributes)
                SELECT %s,
                    CASE WHEN i = %s THEN NULL ELSE
                        ST_SetSRID(ST_MakePoint(-120.0 + i * 0.001, 35.0 + i * 0.0001), 4326) END,
                    jsonb_build_object('name', 'fixture-' || i, 'count', i,
                        'ratio', i * 1.25, 'description', NULL)
                FROM generate_series(1, %s) AS i
                """,
                (test_layer_id, row_count, row_count),
            )
            connection.commit()

        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "1=1", "f": "parquet", "outSR": out_sr,
                    "resultRecordCount": row_count, "orderByFields": "objectid ASC"},
        )
        _require_parquet_available(response)
        frame = gpd.read_parquet(io.BytesIO(response.content))
        assert frame.crs.equals(pyproj.CRS.from_epsg(out_sr), ignore_axis_order=True)
        assert frame["objectid"].tolist() == list(range(1, row_count + 1))
        table_rows = pyarrow_parquet.read_table(io.BytesIO(response.content)).to_pylist()
        geo = _read_geo_metadata(response.content)
        column = geo["columns"][geo["primary_column"]]
        assert column["geometry_types"] == ["Point"]
        covering = column["covering"]["bbox"]
        bbox_column = covering["xmin"][0]
        for ordinate in ("xmin", "ymin", "xmax", "ymax"):
            assert covering[ordinate] == [bbox_column, ordinate]

        for index, row in enumerate(frame.itertuples(), start=1):
            assert row.name == f"fixture-{index}"
            assert row.count == index
            assert row.ratio == index * 1.25
            assert row.description is None
            bbox = table_rows[index - 1][bbox_column]
            if index == row_count:
                assert row.geometry is None
                assert bbox is None
                continue
            x, y = -120.0 + index * 0.001, 35.0 + index * 0.0001
            if out_sr == 3857:
                # Spherical Web Mercator equations, independent of Honua and PROJ.
                x = 6378137.0 * math.radians(x)
                y = 6378137.0 * math.log(math.tan(math.pi / 4 + math.radians(y) / 2))
            assert row.geometry.geom_type == "Point"
            assert row.geometry.x == pytest.approx(x, abs=1e-7, rel=0)
            assert row.geometry.y == pytest.approx(y, abs=1e-7, rel=0)
            assert bbox == pytest.approx(dict(xmin=x, ymin=y, xmax=x, ymax=y), abs=1e-7, rel=0)
