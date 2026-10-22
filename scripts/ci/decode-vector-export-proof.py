#!/usr/bin/env python3
"""Independent GDAL/OGR decoding of files written by Honua, never by GDAL.

Expected coordinates and attributes are hand specified, not read from the
producer, a Honua import reader, a reference query, or an output snapshot.
"""
import csv
import json
from pathlib import Path
import sqlite3
import sys
from osgeo import gdal, ogr

if not __debug__:
    raise RuntimeError("Independent fidelity assertions must not run with Python optimization enabled")
gdal.UseExceptions()
ogr.UseExceptions()
ROOT = Path(sys.argv[1])
EXPECTED = {
    "point": [(1, 2, 30, 40)],
    "line": [[(1, 2, 30, 40), (3, 4, 50, 60)], [(5, 6, 70, 80), (7, 8, 90, 100)]],
    "polygon": [[(0, 0, 10, 0), (4, 0, 10, 0), (4, 4, 10, 0), (0, 4, 10, 0), (0, 0, 10, 0)],
                [(1, 1, 10, 0), (1, 2, 10, 0), (2, 2, 10, 0), (2, 1, 10, 0), (1, 1, 10, 0)]],
}


def vertices(geometry):
    return tuple(geometry.GetPointZM(i) for i in range(geometry.GetPointCount()))


def canonical_path(points):
    points = tuple(points)
    return min(points, tuple(reversed(points)))


def canonical_ring(points):
    points = tuple(points)
    assert len(points) >= 4 and points[0] == points[-1], "ring must remain closed"
    points = points[:-1]
    return min(direction[index:] + direction[:index]
               for direction in (points, tuple(reversed(points)))
               for index in range(len(points)))


def check_geometry(name, geometry):
    assert geometry is not None
    assert geometry.GetGeometryName() == {"point": "POINT", "line": "MULTILINESTRING", "polygon": "POLYGON"}[name]
    assert geometry.Is3D()
    assert bool(geometry.IsMeasured()) == (name != "polygon"), (name, geometry.ExportToIsoWkt())
    if name == "polygon":
        assert geometry.GetGeometryCount() == 2, "exterior and hole must remain separate"
        assert canonical_ring(vertices(geometry.GetGeometryRef(0))) == canonical_ring(EXPECTED[name][0]), \
            "polygon exterior membership"
        assert sorted(canonical_ring(vertices(geometry.GetGeometryRef(index))) for index in range(1, 2)) == \
            sorted(canonical_ring(ring) for ring in EXPECTED[name][1:]), "polygon hole membership"
        assert geometry.GetArea() == 15, "4x4 exterior minus 1x1 hole"
    elif name == "line":
        assert geometry.GetGeometryCount() == 2, "disjoint line parts must remain separate"
        assert sorted(canonical_path(vertices(geometry.GetGeometryRef(index))) for index in range(2)) == \
            sorted(canonical_path(part) for part in EXPECTED[name]), "line component membership"
    else:
        assert vertices(geometry) == tuple(EXPECTED[name]), "point ordinates"


def verify_structural_challenges():
    # Reordering whole parts, reversing paths, and rotating/reversing rings are
    # legitimate serialization differences. Reconnecting endpoints or swapping
    # the exterior with its hole changes the geometry despite identical vertices.
    check_geometry("line", ogr.CreateGeometryFromWkt(
        "MULTILINESTRING ZM ((7 8 90 100,5 6 70 80),(3 4 50 60,1 2 30 40))"))
    check_geometry("polygon", ogr.CreateGeometryFromWkt(
        "POLYGON Z ((4 4 10,4 0 10,0 0 10,0 4 10,4 4 10),"
        "(2 2 10,1 2 10,1 1 10,2 1 10,2 2 10))"))
    challenges = [
        ("line", "MULTILINESTRING ZM ((1 2 30 40,5 6 70 80),(3 4 50 60,7 8 90 100))",
         "line component membership"),
        ("polygon", "POLYGON Z ((1 1 10,1 2 10,2 2 10,2 1 10,1 1 10),"
         "(0 0 10,4 0 10,4 4 10,0 4 10,0 0 10))", "polygon exterior membership"),
    ]
    for name, wkt, reason in challenges:
        try:
            check_geometry(name, ogr.CreateGeometryFromWkt(wkt))
        except AssertionError as error:
            assert reason in str(error), f"Wrong structural rejection reason: {error}"
        else:
            raise AssertionError(f"Oracle accepted changed {reason}")
    return len(challenges)


structural_challenges = verify_structural_challenges()
checks = []
for name in EXPECTED:
    directory = ROOT / name
    # CSV's contract carries CRS through the request, not a file sidecar. Empty
    # cells represent missing values; the CSV format cannot distinguish null/empty.
    with (directory / "export.csv").open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    assert len(rows) == 2
    assert rows[0]["label"] == 'alpha, "quoted"'
    assert float(rows[0]["amount"]) == 7.25 and rows[0]["missing"] == ""
    assert rows[1] == {"label": "unlocated", "amount": "0", "missing": "", "WKT": ""}
    check_geometry(name, ogr.CreateGeometryFromWkt(rows[0]["WKT"]))
    checks.append({"format": "csv", "fixture": name, "measured_count": 2, "measured_delta": 0})
    for suffix in ("shp", "gpkg"):
        print(f"Checking {name}/export.{suffix}", flush=True)
        dataset = gdal.OpenEx(str(directory / f"export.{suffix}"), gdal.OF_VECTOR)
        assert dataset is not None
        layer = dataset.GetLayer(0)
        assert layer.GetFeatureCount() == 2
        srs = layer.GetSpatialRef()
        assert srs is not None and srs.GetAuthorityCode(None) == "4326"
        rows = list(layer)
        assert rows[0].GetField("label") == 'alpha, "quoted"'
        assert rows[0].GetField("amount") == 7.25, "amount mismatch"
        assert rows[0].GetField("missing") is None
        check_geometry(name, rows[0].GetGeometryRef())
        assert rows[1].GetField("label") == "unlocated"
        assert rows[1].GetField("amount") == 0
        assert rows[1].GetField("missing") is None
        assert rows[1].GetGeometryRef() is None
        checks.append({"format": suffix, "fixture": name, "measured_count": 2, "measured_delta": 0})
        dataset = None
    with sqlite3.connect(directory / "export.gpkg") as connection:
        assert connection.execute("PRAGMA application_id").fetchone()[0] == 0x47504B47
        assert connection.execute("SELECT srs_id FROM gpkg_contents").fetchall() == [(4326,)]
        assert connection.execute("SELECT srs_id,z,m FROM gpkg_geometry_columns").fetchall() == [
            (4326, 1, 0 if name == "polygon" else 1)]

receipt = {"reader": "GDAL " + gdal.VersionInfo("RELEASE_NAME"), "checks": checks,
           "structural_challenges_rejected": structural_challenges}
(ROOT / "independent-reader.json").write_text(json.dumps(receipt, indent=2))
print(json.dumps(receipt, indent=2))
