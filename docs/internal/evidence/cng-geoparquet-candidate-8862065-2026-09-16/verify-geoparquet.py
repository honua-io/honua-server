#!/usr/bin/env python3
"""Decode a live FeatureServer f=parquet response and assert it against the
independently specified docker/cng/seed.sql fixture.

Every expected value below is transcribed from the seed INSERT, not from the
server's output: the check fails if the payload drifts from the fixture.
Reads the file three ways -- raw PyArrow (with a hand-written WKB point parser),
GeoPandas/Shapely, and the GeoParquet `geo` metadata -- so a defect in any one
client library cannot make the assertion vacuous.

usage: verify-geoparquet.py <cng.parquet> <report.json>
"""
from __future__ import annotations

import json
import struct
import sys
from datetime import datetime, timezone

# docker/cng/seed.sql, "Deterministic features spanning antimeridian / equator /
# poles". (lon, lat, name, category, population, ratio, active, observed_at)
EXPECTED_ROWS = [
    (-122.4194, 37.7749, "Harbor City", "city", 1000000, 0.91, True, "2026-01-02T03:04:05Z"),
    (-122.2711, 37.8044, "Baytown", "city", 430000, 0.42, True, "2026-02-11T12:00:00Z"),
    (0.0, 51.4779, "Meridian Marker", "reference", 0, 0.00, False, "2026-03-21T00:00:00Z"),
    (-75.0, 0.0, "Equator Station", "reference", 0, 0.50, True, "2026-04-01T06:30:00Z"),
    (179.5, 0.5, "Dateline Post", "reference", 0, 0.75, False, "2026-05-09T18:45:00Z"),
    (0.0, 86.0, "Polar Outpost", "reference", 0, 0.10, True, "2026-06-15T09:15:00Z"),
]

# honua.layers row 1000 declares geometry_type 'Point', so the writer must take the
# concrete, non-empty geometry-types branch -- the one #4747 could not serialize
# under Native AOT. A `[]` here would mean the fixture never exercised the defect.
EXPECTED_GEO = {
    "version": "1.1.0",
    "primary_column": "geometry",
    "columns": {
        "geometry": {
            "encoding": "WKB",
            "geometry_types": ["Point"],
            "covering": {
                "bbox": {
                    "xmin": ["bbox", "xmin"],
                    "ymin": ["bbox", "ymin"],
                    "xmax": ["bbox", "xmax"],
                    "ymax": ["bbox", "ymax"],
                }
            },
        }
    },
}

EXPECTED_BBOX = (-122.4194, 0.0, 179.5, 86.0)  # min/max over EXPECTED_ROWS
TOL = 1e-9

failures: list[str] = []
checks: list[dict] = []


def check(name: str, ok: bool, detail: str) -> None:
    checks.append({"check": name, "ok": bool(ok), "detail": detail})
    if not ok:
        failures.append(f"{name}: {detail}")


def parse_wkb_point(blob: bytes) -> tuple[float, float]:
    """Minimal WKB reader: byte order, type 1 (Point), then X and Y."""
    if len(blob) != 21:
        raise ValueError(f"expected a 21-byte 2D WKB point, got {len(blob)} bytes")
    endian = "<" if blob[0] == 1 else ">"
    (geom_type,) = struct.unpack_from(endian + "I", blob, 1)
    if geom_type != 1:
        raise ValueError(f"expected WKB geometry type 1 (Point), got {geom_type}")
    return struct.unpack_from(endian + "dd", blob, 5)


def main(path: str, report_path: str) -> int:
    import pyarrow.parquet as pq

    pf = pq.ParquetFile(path)
    table = pf.read()

    check("pyarrow/row-count", table.num_rows == len(EXPECTED_ROWS),
          f"{table.num_rows} rows (expected {len(EXPECTED_ROWS)})")

    # --- GeoParquet metadata -------------------------------------------------
    raw_geo = table.schema.metadata.get(b"geo") if table.schema.metadata else None
    check("metadata/geo-present", raw_geo is not None, "`geo` key in the Parquet schema metadata")
    geo = json.loads(raw_geo.decode()) if raw_geo else {}
    check("metadata/geo-exact", geo == EXPECTED_GEO,
          json.dumps(geo, sort_keys=True) if geo != EXPECTED_GEO else "matches the expected document")
    types = geo.get("columns", {}).get("geometry", {}).get("geometry_types")
    check("metadata/geometry-types-non-empty", types == ["Point"],
          f"geometry_types={types!r} (must be the concrete non-empty branch #4747 could not serialize)")
    check("metadata/geo-is-valid-json", isinstance(raw_geo, (bytes, bytearray))
          and raw_geo.decode("utf-8") and json.loads(raw_geo.decode("utf-8")) == geo,
          "the `geo` value round-trips as UTF-8 JSON (escaping preserved)")

    # --- Attribute values and geometry, read via raw PyArrow -----------------
    cols = {name: table.column(name).to_pylist() for name in table.schema.names}
    by_name = {}
    for idx, name in enumerate(cols["name"]):
        by_name[name] = idx
    check("pyarrow/names", sorted(by_name) == sorted(r[2] for r in EXPECTED_ROWS),
          f"decoded names {sorted(by_name)}")

    for lon, lat, name, category, population, ratio, active, observed_at in EXPECTED_ROWS:
        if name not in by_name:
            check(f"row[{name}]", False, "row missing from the decoded file")
            continue
        i = by_name[name]
        x, y = parse_wkb_point(cols["geometry"][i])
        check(f"row[{name}]/ordinates", abs(x - lon) <= TOL and abs(y - lat) <= TOL,
              f"WKB point ({x!r}, {y!r}) vs seeded ({lon!r}, {lat!r})")
        check(f"row[{name}]/category", cols["category"][i] == category,
              f"{cols['category'][i]!r} vs {category!r}")
        check(f"row[{name}]/population", cols["population"][i] == population,
              f"{cols['population'][i]!r} vs {population!r}")
        check(f"row[{name}]/ratio", abs(cols["ratio"][i] - ratio) <= TOL,
              f"{cols['ratio'][i]!r} vs {ratio!r}")
        check(f"row[{name}]/active", cols["active"][i] is active,
              f"{cols['active'][i]!r} vs {active!r}")
        want_ts = datetime.strptime(observed_at, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
        got_ts = cols["observed_at"][i]
        if got_ts is not None and got_ts.tzinfo is None:
            got_ts = got_ts.replace(tzinfo=timezone.utc)
        check(f"row[{name}]/observed_at", got_ts == want_ts, f"{got_ts!r} vs {want_ts!r}")
        # The bbox covering columns the `geo` metadata advertises must agree with
        # the geometry they cover; for a point all four ordinates are the point.
        bb = cols["bbox"][i]
        check(f"row[{name}]/covering-bbox",
              all(abs(bb[k] - v) <= TOL for k, v in
                  (("xmin", lon), ("xmax", lon), ("ymin", lat), ("ymax", lat))),
              f"{bb!r} vs point ({lon!r}, {lat!r})")

    # --- Second, independent decode: GeoPandas / Shapely ---------------------
    try:
        import geopandas

        gdf = geopandas.read_parquet(path)
        check("geopandas/row-count", len(gdf) == len(EXPECTED_ROWS), f"{len(gdf)} rows")
        gseries = {row["name"]: row.geometry for _, row in gdf.iterrows()}
        for lon, lat, name, *_ in EXPECTED_ROWS:
            geom = gseries.get(name)
            check(f"geopandas[{name}]", geom is not None and geom.geom_type == "Point"
                  and abs(geom.x - lon) <= TOL and abs(geom.y - lat) <= TOL,
                  f"{geom!r} vs seeded ({lon!r}, {lat!r})")
        total = gdf.total_bounds
        check("geopandas/total-bounds",
              all(abs(total[i] - EXPECTED_BBOX[i]) <= TOL for i in range(4)),
              f"{list(total)} vs {list(EXPECTED_BBOX)}")
        # The writer omits `crs`, which GeoParquet 1.1 defines as OGC:CRS84
        # (longitude/latitude WGS 84) -- the seeded layer's SRID 4326 in the axis
        # order the WKB above uses. `to_epsg()` is deliberately not used: it
        # returns None for CRS84 because its axis order differs from EPSG:4326.
        check("geopandas/crs", gdf.crs is not None and gdf.crs.equals("OGC:CRS84"),
              f"crs={gdf.crs} (GeoParquet default for an omitted `crs`)")
    except ImportError as exc:  # pragma: no cover - the runner installs it
        check("geopandas/available", False, f"geopandas not importable: {exc}")

    report = {
        "artifact": path,
        "rows": table.num_rows,
        "geo": geo,
        "checks": checks,
        "failed": failures,
        "verdict": "pass" if not failures else "fail",
    }
    with open(report_path, "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2, sort_keys=False)
        handle.write("\n")

    for entry in checks:
        print(f"{'PASS' if entry['ok'] else 'FAIL'}  {entry['check']}: {entry['detail']}")
    print(f"\n{len(checks) - len(failures)}/{len(checks)} checks passed")
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2]))
