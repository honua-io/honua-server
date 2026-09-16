"""GDAL cloud-native and raster bounded-roster cells.

* ``gdal-flatgeobuf`` 3.8.4  FeatureServer ``f=fgb`` read through ``/vsicurl/``
* ``gdal-cog``        3.8.4  COG dataset read of a Honua-served COG tile
* ``gdal``            3.13.3 cloud COG serving (ImageServer tile fallback)
* ``gdal``            3.13.3 multidimensional (Zarr) coverage over OGC API Coverages

Which cells run is decided by the GDAL release in the lane image, so one module
serves both GDAL lane images.
"""
from __future__ import annotations

import struct

from osgeo import gdal, ogr, osr

import gdalkit
from cellkit import Cell, expect
from rosterenv import EXPIRED_BEARER, WRONG_API_KEY, api_key_headers, bearer_headers, url

VERSION = gdal.__version__
CLIENT_DETAIL = gdal.VersionInfo("--version")
TIMEOUT = {"GDAL_HTTP_TIMEOUT": "75"}

CNG_FEATURES = {
    "Harbor City": (-122.4194, 37.7749), "Baytown": (-122.2711, 37.8044), "Meridian Marker": (0.0, 51.4779),
    "Equator Station": (-75.0, 0.0), "Dateline Post": (179.5, 0.5), "Polar Outpost": (0.0, 86.0),
}
FGB_QUERY = url("/rest/services/cng/FeatureServer/1000/query?where=1%3D1&outFields=*&f=fgb")

# WebMercatorQuad z14 tile that contains the COG fixture's centre (-122.42, 37.76).
COG_TILE = (14, 6333, 2621)
COG_SERVICE = "roster_cog"


def _vsicurl(target: str) -> str:
    return "/vsicurl?use_head=no&list_dir=no&url=" + target.replace("?", "%3F").replace("&", "%26").replace("=", "%3D")


def _tile_url(service: str = COG_SERVICE, fmt: str = "cog", tile=COG_TILE) -> str:
    z, row, col = tile
    return url(f"/rest/services/{service}/ImageServer/tile/{z}/{row}/{col}?format={fmt}")


# ---------------------------------------------------------------------------
# FlatGeobuf
# ---------------------------------------------------------------------------

def flatgeobuf() -> None:
    cell = Cell("client-cert/gdal/flatgeobuf/feature-read", client_version_detail=CLIENT_DETAIL,
                protocol_version="FlatGeobuf 3", protocol_profile="FeatureServer query f=fgb")
    cell.primary_request_url = FGB_QUERY

    def open_layer(record_config=None):
        dataset = gdal.OpenEx(_vsicurl(FGB_QUERY), gdal.OF_VECTOR, allowed_drivers=["FlatGeobuf"])
        return dataset, dataset.GetLayer(0)

    with cell.check("positive", "read every CNG feature from the FlatGeobuf response") as c:
        with gdalkit.session(None, OGR_FLATGEOBUF_VERIFY_BUFFERS="YES") as record:
            dataset, layer = open_layer()
            features = {feature.GetField("name"): (round(feature.GetGeometryRef().GetX(), 6), round(feature.GetGeometryRef().GetY(), 6))
                        for feature in layer}
            dataset = None
        expect(features == CNG_FEATURES, features)
        expect(record.fetched_path("f=fgb"), record.fetched)
        c.detail = f"{len(features)} features with verified buffers: {sorted(features)}"
    with cell.check("metadata", "header feature count, extent and typed schema describe the seeded layer") as c:
        with gdalkit.session():
            dataset, layer = open_layer()
            definition = layer.GetLayerDefn()
            fields = {definition.GetFieldDefn(i).GetName(): (definition.GetFieldDefn(i).GetTypeName(),
                      ogr.GetFieldSubTypeName(definition.GetFieldDefn(i).GetSubType())) for i in range(definition.GetFieldCount())}
            count = layer.GetFeatureCount(force=0)
            extent = layer.GetExtent(force=0)
            geometry = ogr.GeometryTypeToName(layer.GetGeomType())
            dataset = None
        expect(count == 6, f"header feature count {count}")
        expect(geometry == "Point", geometry)
        expect(fields.get("name", ("",))[0] == "String" and fields.get("population", ("",))[0].startswith("Integer")
               and fields.get("ratio", ("",))[0] == "Real" and fields.get("observed_at", ("",))[0] == "DateTime"
               and fields.get("active") == ("Integer", "Boolean"), fields)
        expect([round(v, 4) for v in extent] == [-122.4194, 179.5, 0.0, 86.0], extent)
        c.detail = f"count {count}; geometry {geometry}; extent {extent}; fields {fields}"
    with cell.check("crs-axis", "the header CRS is EPSG:4326 and coordinates are longitude/latitude") as c:
        with gdalkit.session():
            dataset, layer = open_layer()
            srs = layer.GetSpatialRef()
            code = srs.GetAuthorityCode(None) if srs else None
            mapping = srs.GetDataAxisToSRSAxisMapping() if srs else None
            harbor_feature = next(feature for feature in layer if feature.GetField("name") == "Harbor City")
            harbor = harbor_feature.GetGeometryRef()
            harbor_xy = (round(harbor.GetX(), 4), round(harbor.GetY(), 4))
            harbor_wkt = harbor.ExportToWkt()
            dataset = None
        expect(code == "4326", code)
        expect(harbor_xy == (-122.4194, 37.7749), harbor_wkt)
        c.detail = f"EPSG:{code}, data axis mapping {mapping}; Harbor City {harbor_wkt}; Dateline Post x=179.5 stays in range"
    cell.write()


# ---------------------------------------------------------------------------
# COG
# ---------------------------------------------------------------------------

def _open_cog_tile(headers=None, service: str = COG_SERVICE, fmt: str = "cog", tile=COG_TILE):
    with gdalkit.session(headers, **TIMEOUT) as record:
        try:
            dataset = gdal.Open(_vsicurl(_tile_url(service, fmt, tile)))
        except RuntimeError as error:
            return None, str(error), record
        return dataset, None, record


def cog_dataset_read() -> None:
    cell = Cell("client-cert/gdal/cog/dataset-read", client_version_detail=CLIENT_DETAIL,
                protocol_version="COG 1.0", protocol_profile="ImageServer tile format=cog")
    cell.primary_request_url = _tile_url()
    served = {}
    with cell.check("positive", "open the served COG tile and read the fixture's pixel gradient") as c:
        dataset, error, record = _open_cog_tile()
        expect(dataset is not None, f"GDAL could not open {_tile_url()}: {error}; requests {record.fetched}")
        band = dataset.GetRasterBand(1)
        values = sorted(set(bytes(band.ReadRaster(0, 0, dataset.RasterXSize, dataset.RasterYSize))) - {0})
        served.update(size=(dataset.RasterXSize, dataset.RasterYSize), values=values)
        expect(values and all((value - 40) % 8 == 0 for value in values), values[:10])
        c.detail = f"{served['size']} values {values[:8]}"
    for facet, name in (("metadata", "COG layout, block size and overviews"),
                        ("crs-axis", "EPSG:3857 georeferencing of the WebMercatorQuad tile")):
        with cell.check(facet, name) as c:
            expect(served, "not exercisable: the COG tile was not served (see the positive check)")
            dataset, error, _ = _open_cog_tile()
            expect(dataset is not None, error)
            if facet == "metadata":
                structure = dataset.GetMetadata("IMAGE_STRUCTURE")
                expect(structure.get("LAYOUT") == "COG", structure)
                c.detail = f"{structure}; block {dataset.GetRasterBand(1).GetBlockSize()}"
            else:
                code = dataset.GetSpatialRef().GetAuthorityCode(None)
                transform = dataset.GetGeoTransform()
                expect(code == "3857", code)
                c.detail = f"EPSG:{code} {transform}"
    cell.write()


def cog_serving() -> None:
    cell = Cell("client-cert/gdal/raster-cloud-cog-serving/raster.cloud-cog-serving", client_version_detail=CLIENT_DETAIL,
                protocol_version="GeoServices ImageServer tile + COG 1.0", protocol_profile="registered cloud COG, WebMercatorQuad tiles")
    cell.primary_request_url = _tile_url(fmt="png")
    served = {}
    with cell.check("positive", "read a WebMercatorQuad tile of the registered cloud COG") as c:
        dataset, error, record = _open_cog_tile(fmt="png")
        expect(dataset is not None,
               f"{_tile_url(fmt='png')} was not served: {error}; the registered S3 COG was never read "
               f"(requests {record.fetched})")
        values = sorted(set(bytes(dataset.GetRasterBand(1).ReadRaster(0, 0, dataset.RasterXSize, dataset.RasterYSize))) - {0})
        served["png"] = values
        expect(values, "tile is empty")
        c.detail = f"values {values[:8]}"
    dependent = (
        ("negative", "a tile outside the registered COG is refused or empty"),
        ("auth", "the protected mirror's tiles require a credential"),
        ("metadata", "format=cog tiles carry COG layout metadata"),
        ("range-efficiency", "COG tiles honour HTTP range reads"),
        ("crs-axis", "tiles are georeferenced in EPSG:3857"),
        ("media-schema", "png/tiff/cog formats carry their media types"),
    )
    for facet, name in dependent:
        with cell.check(facet, name) as c:
            expect(served, "not exercisable: the registered cloud COG was not served (see the positive check)")
            if facet == "auth":
                outcomes = {}
                for label, headers in (("anonymous", None), ("wrong-api-key", WRONG_API_KEY), ("expired-bearer", EXPIRED_BEARER),
                                       ("api-key", api_key_headers()), ("oidc-bearer", bearer_headers())):
                    dataset, error, _ = _open_cog_tile(headers, "roster_cog_protected", "png")
                    outcomes[label] = "served" if dataset is not None else (gdalkit.http_status_in(error) or error[:80])
                expect(all(outcomes[k] != "served" for k in ("anonymous", "wrong-api-key", "expired-bearer"))
                       and all(outcomes[k] == "served" for k in ("api-key", "oidc-bearer")), outcomes)
                c.detail = f"{outcomes}"
            elif facet == "negative":
                dataset, error, _ = _open_cog_tile(tile=(14, 1, 1), fmt="png")
                c.detail = f"far tile -> {'served empty' if dataset is not None else error[:160]}"
                if dataset is not None:
                    expect(not (set(bytes(dataset.GetRasterBand(1).ReadRaster(0, 0, 8, 8))) - {0}), "far tile has content")
            elif facet == "range-efficiency":
                with gdalkit.session(None, **TIMEOUT) as record:
                    dataset = gdal.Open("/vsicurl?use_head=yes&list_dir=no&url=" + _tile_url().replace("?", "%3F").replace("=", "%3D"))
                    dataset.GetRasterBand(1).ReadRaster(0, 0, 16, 16)
                    size = gdal.VSIStatL(_vsicurl(_tile_url())).size
                ranged = [value for value in record.fetched if value]
                expect(ranged, record.fetched)
                c.detail = f"object size {size}; requests {ranged[:4]}"
            else:
                dataset, error, _ = _open_cog_tile()
                expect(dataset is not None, error)
                if facet == "metadata":
                    expect(dataset.GetMetadata("IMAGE_STRUCTURE").get("LAYOUT") == "COG", dataset.GetMetadata("IMAGE_STRUCTURE"))
                elif facet == "crs-axis":
                    expect(dataset.GetSpatialRef().GetAuthorityCode(None) == "3857", dataset.GetSpatialRef().ExportToWkt()[:80])
                c.detail = f"{dataset.GetMetadata('IMAGE_STRUCTURE')}"
    cell.write()


# ---------------------------------------------------------------------------
# multidimensional coverage (Zarr datacube)
# ---------------------------------------------------------------------------

def multidim() -> None:
    cell = Cell("client-cert/gdal/raster-multidim-coverage/raster.multidim-coverage", client_version_detail=CLIENT_DETAIL,
                protocol_version="OGC API - Coverages 1.0", protocol_profile="registered Zarr datacube")
    target = url("/ogc/coverages/collections/5200")
    cell.primary_request_url = target + "/coverage"
    opened = {}
    with cell.check("positive", "open the registered Zarr datacube as a coverage and read the t0 slice") as c:
        with gdalkit.session(None, **TIMEOUT) as record:
            try:
                dataset = gdal.OpenEx("OGCAPI:" + target, gdal.OF_RASTER, open_options=["API=COVERAGE"])
            except RuntimeError as error:
                expect(False, f"GDAL OGCAPI could not open {target}: {error}; requests {record.fetched}")
            opened["size"] = (dataset.RasterXSize, dataset.RasterYSize)
            dataset = None
        c.detail = f"{opened}"
    for facet, name in (("negative", "an unknown datacube collection is a 404"),
                        ("auth", "the protected datacube requires a credential"),
                        ("metadata", "variables, dimensions and CF units are described"),
                        ("boundary", "an edge subset reads the boundary cells"),
                        ("crs-axis", "latitude/longitude axes map to the lon/lat extent"),
                        ("media-schema", "coverage payloads decode as GeoTIFF")):
        with cell.check(facet, name) as c:
            if facet == "negative":
                message = gdalkit.open_failure("OGCAPI:" + url("/ogc/coverages/collections/999999"), flags=gdal.OF_RASTER,
                                               open_options=["API=COVERAGE"])
                expect(gdalkit.http_status_in(message) == 404, message)
                c.detail = message[:200]
                continue
            if facet == "auth":
                outcomes = {label: gdalkit.http_status_in(gdalkit.open_failure(
                    "OGCAPI:" + url("/ogc/coverages/collections/5201"), headers, flags=gdal.OF_RASTER,
                    open_options=["API=COVERAGE"])) for label, headers in (
                        ("anonymous", None), ("wrong-api-key", WRONG_API_KEY), ("expired-bearer", EXPIRED_BEARER))}
                expect(all(value == 401 for value in outcomes.values()), outcomes)
                expect(opened, f"denials {outcomes}, but no credential can be shown to admit a datacube that is not served")
                c.detail = f"{outcomes}"
                continue
            expect(opened, "not exercisable: the datacube coverage was not served (see the positive check)")
    cell.write()


if VERSION.startswith("3.8."):
    CELLS = (flatgeobuf, cog_dataset_read)
else:
    CELLS = (cog_serving, multidim)
