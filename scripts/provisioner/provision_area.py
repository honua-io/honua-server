#!/usr/bin/env python3
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Open-data area-import provisioner -- first slice.

Given an open-data SOURCE/PRODUCT from the curated catalog and an AREA
(a bbox or a US Census GEOID), this tool:

  1. resolves the fetch recipe + URL from data/open-data-catalog/catalog.json,
  2. fetches the source for that area,
  3. clips/filters it to the exact area (where the recipe requires it),
  4. imports it into a running Honua Server via the admin file-import API
     (POST /api/v1/admin/import/upload, multipart -- the same 13-format
     pipeline used by the console),
  5. publishes the imported table as a Honua layer
     (POST /api/v1/admin/connections/{id}/layers), and
  6. (optionally) queues a PMTiles tiling job
     (POST /api/v1/admin/tile-operations/jobs) which runs in-process or, when
     a batch backend is configured, on GP-on-Batch (Fargate Spot, scale-to-zero).

It reuses existing server primitives only -- no new server code. Steps 1-4 are
real and runnable for the seeded `census-tiger` and `osm-geofabrik` sources;
where a step cannot be fully automated in one slice it is surfaced as an
explicit, actionable message rather than faked.

Auth: admin X-API-Key (env HONUA_ADMIN_API_KEY) against --server (default
http://localhost:8080).

External tools: `curl` (always) and `ogr2ogr` (GDAL; required only for the
clip/convert step on shapefile sources). The tool degrades gracefully and
tells you exactly what to install if a dependency is missing.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.parse
import urllib.request
from pathlib import Path

CATALOG = (
    Path(__file__).resolve().parents[2]
    / "data"
    / "open-data-catalog"
    / "catalog.json"
)


# --------------------------------------------------------------------------- #
# Catalog
# --------------------------------------------------------------------------- #
def load_catalog() -> dict:
    with CATALOG.open(encoding="utf-8") as fh:
        return json.load(fh)


def find_source(catalog: dict, source_id: str) -> dict:
    for src in catalog["sources"]:
        if src["id"] == source_id:
            return src
    ids = ", ".join(s["id"] for s in catalog["sources"])
    raise SystemExit(f"unknown source '{source_id}'. known sources: {ids}")


def find_product(source: dict, product_id: str) -> dict:
    for prod in source["products"]:
        if prod["product"] == product_id:
            return prod
    ids = ", ".join(p["product"] for p in source["products"])
    raise SystemExit(
        f"unknown product '{product_id}' for source '{source['id']}'. "
        f"products: {ids}"
    )


# --------------------------------------------------------------------------- #
# AREA parsing
# --------------------------------------------------------------------------- #
class Area:
    """A normalized area selector derived from the --area argument.

    Forms accepted:
      bbox:minLon,minLat,maxLon,maxLat
      geoid:DDDDD            (5-digit county GEOID = stateFips + countyCode)
      geoid:DD               (2-digit state FIPS)
    """

    def __init__(self, kind: str, raw: str):
        self.kind = kind
        self.raw = raw
        self.bbox: tuple[float, float, float, float] | None = None
        self.state_fips: str | None = None
        self.county_geoid: str | None = None
        self.place_geoid: str | None = None

    @classmethod
    def parse(cls, value: str) -> "Area":
        if value.startswith("bbox:"):
            parts = value[len("bbox:"):].split(",")
            if len(parts) != 4:
                raise SystemExit("bbox must be minLon,minLat,maxLon,maxLat")
            nums = tuple(float(p) for p in parts)
            area = cls("bbox", value)
            area.bbox = nums  # type: ignore[assignment]
            return area
        if value.startswith("geoid:"):
            code = value[len("geoid:"):].strip()
            if not code.isdigit():
                raise SystemExit("geoid must be numeric (2, 5, or 7 digits)")
            area = cls("geoid", value)
            if len(code) == 2:
                area.state_fips = code
            elif len(code) == 5:
                area.county_geoid = code
                area.state_fips = code[:2]
            elif len(code) == 7:
                area.place_geoid = code
                area.state_fips = code[:2]
            else:
                raise SystemExit("geoid must be 2 (state), 5 (county), or 7 (place) digits")
            return area
        raise SystemExit("--area must start with 'bbox:' or 'geoid:'")

    def overpass_bbox(self) -> str:
        # Overpass expects south,west,north,east
        if not self.bbox:
            raise SystemExit("this product requires a bbox area")
        w, s, e, n = self.bbox
        return f"{s},{w},{n},{e}"

    def ogr_clipsrc(self) -> list[str]:
        if not self.bbox:
            return []
        w, s, e, n = self.bbox
        return ["-clipsrc", str(w), str(s), str(e), str(n)]


# --------------------------------------------------------------------------- #
# Fetch + clip
# --------------------------------------------------------------------------- #
def render_url(template: str, area: Area) -> str:
    subs = {
        "stateFips": area.state_fips or "",
        "countyGeoid": area.county_geoid or "",
        "placeGeoid": area.place_geoid or "",
        "bboxSWNE": area.overpass_bbox() if area.kind == "bbox" else "",
    }
    out = template
    for key, val in subs.items():
        out = out.replace("{" + key + "}", val)
    if "{" in out:
        missing = re.findall(r"\{(\w+)\}", out)
        raise SystemExit(
            f"fetch URL still has unfilled placeholders {missing}; "
            f"the --area form does not satisfy this product's recipe"
        )
    return out


def http_download(url: str, dest: Path) -> None:
    print(f"  fetch: {url}")
    req = urllib.request.Request(url, headers={"User-Agent": "honua-provisioner/1.0"})
    with urllib.request.urlopen(req, timeout=180) as resp, dest.open("wb") as fh:
        shutil.copyfileobj(resp, fh)
    print(f"  -> {dest} ({dest.stat().st_size:,} bytes)")


def have(tool: str) -> bool:
    return shutil.which(tool) is not None


def fetch_and_prepare(product: dict, area: Area, workdir: Path) -> Path:
    """Fetch the source for the area and return a path to a GeoJSON ready for import.

    Returns a .geojson (always one of Honua's import formats) so the import step
    is uniform. Shapefile sources are converted/clipped via ogr2ogr.
    """
    fetch = product.get("fetch")
    if not fetch:
        raise SystemExit(
            f"product '{product['product']}' has no fetch recipe -- it is a "
            f"placeholder source. See docs/guides/open-data-provisioner.md."
        )
    kind = fetch["kind"]
    out_geojson = workdir / f"{product['product']}.geojson"

    if kind == "overpass":
        # Overpass returns OSM JSON; convert to GeoJSON via ogr2ogr (GDAL has an
        # OSM/Overpass driver via the "OSM" vsicurl path is heavy, so we fetch
        # the JSON and let ogr2ogr read it as GeoJSON-ish). Simpler + robust:
        # use the Overpass 'out geom' result which ogr2ogr can read with the
        # OSM driver from a .osm file. We request JSON and convert.
        url = render_url(fetch["urlTemplate"], area)
        raw = workdir / f"{product['product']}.overpass.json"
        http_download(url, raw)
        if not have("ogr2ogr"):
            raise SystemExit(
                "ogr2ogr (GDAL) is required to convert Overpass JSON to GeoJSON.\n"
                "  install: apt-get install gdal-bin  (or: brew install gdal)\n"
                f"  raw Overpass result left at: {raw}"
            )
        # GDAL reads Overpass JSON via the OSM driver only from .osm; for the
        # JSON form we round-trip through the GeoJSON driver which understands
        # the 'out geom' geometry. If that fails, the message points at osmium.
        rc = subprocess.run(
            ["ogr2ogr", "-f", "GeoJSON", str(out_geojson), str(raw)],
            capture_output=True, text=True,
        )
        if rc.returncode != 0:
            raise SystemExit(
                "could not convert Overpass result with ogr2ogr.\n"
                "  TODO(slice-2): use osmtogeojson or osmium to convert, or switch\n"
                "  this product to the Geofabrik .osm.pbf + osmium extract path.\n"
                f"  ogr2ogr stderr: {rc.stderr.strip()}\n"
                f"  raw result: {raw}"
            )
        return out_geojson

    if kind == "http-template":
        url = render_url(fetch["urlTemplate"], area)
        suffix = ".zip" if product.get("format") == "shapefile-zip" else ".dat"
        dl = workdir / f"{product['product']}{suffix}"
        http_download(url, dl)
        if product.get("format") == "shapefile-zip":
            if not have("ogr2ogr"):
                raise SystemExit(
                    "ogr2ogr (GDAL) is required to clip/convert the shapefile.\n"
                    "  install: apt-get install gdal-bin  (or: brew install gdal)\n"
                    f"  downloaded zip left at: {dl}"
                )
            args = ["ogr2ogr", "-f", "GeoJSON"]
            # Row-filter by GEOID for county/state polygon sources when clip=true.
            if fetch.get("clip", True) and area.county_geoid and product["product"] == "county-boundaries":
                args += ["-where", f"GEOID = '{area.county_geoid}'"]
            elif fetch.get("clip", True) and area.kind == "bbox":
                args += area.ogr_clipsrc()
            args += [str(out_geojson), f"/vsizip/{dl}"]
            rc = subprocess.run(args, capture_output=True, text=True)
            if rc.returncode != 0:
                raise SystemExit(f"ogr2ogr failed: {rc.stderr.strip()}")
            return out_geojson
        # Unhandled non-shapefile http-template format.
        raise SystemExit(
            f"format '{product.get('format')}' fetch is not wired in this slice "
            f"(TODO). Downloaded file: {dl}"
        )

    if kind == "geofabrik-extract":
        raise SystemExit(
            "geofabrik-extract (regional .osm.pbf clip) is a documented TODO for "
            "slice 2. Use the Overpass bbox product for small areas today.\n"
            "  plan: download nearest Geofabrik region .osm.pbf, then\n"
            "  `osmium extract -b minlon,minlat,maxlon,maxlat region.osm.pbf -o area.osm.pbf`\n"
            "  then ogr2ogr -f GeoJSON out.geojson area.osm.pbf"
        )

    raise SystemExit(f"unknown fetch kind '{kind}'")


# --------------------------------------------------------------------------- #
# Honua Server admin API
# --------------------------------------------------------------------------- #
class Honua:
    def __init__(self, server: str, api_key: str):
        self.server = server.rstrip("/")
        self.api_key = api_key

    def _curl(self, args: list[str]) -> tuple[int, str]:
        full = ["curl", "-sS", "-H", f"X-API-Key: {self.api_key}", *args]
        rc = subprocess.run(full, capture_output=True, text=True)
        if rc.returncode != 0:
            raise SystemExit(f"curl failed: {rc.stderr.strip()}")
        return rc.returncode, rc.stdout

    def import_upload(self, geojson: Path, table: str, overwrite: bool) -> dict:
        url = f"{self.server}/api/v1/admin/import/upload"
        args = [
            "-X", "POST", url,
            "-F", f"file=@{geojson};type=application/geo+json",
            "-F", f"tableName={table}",
            "-F", f"overwriteExisting={'true' if overwrite else 'false'}",
            "-F", "targetSrid=4326",
        ]
        _, out = self._curl(args)
        return _parse_json(out, "import/upload")

    def publish_layer(self, connection_id: str, table: str, layer_name: str,
                      geometry_type: str, schema: str) -> dict:
        url = f"{self.server}/api/v1/admin/connections/{connection_id}/layers/"
        body = json.dumps({
            "schema": schema,
            "table": table,
            "layerName": layer_name,
            "geometryType": geometry_type,
            "srid": 4326,
            "enabled": True,
        })
        args = ["-X", "POST", url, "-H", "Content-Type: application/json", "-d", body]
        _, out = self._curl(args)
        return _parse_json(out, "layers")

    def tile_job(self, service_id: str, layer_id: int | None,
                 bbox: tuple[float, ...] | None, minz: int, maxz: int) -> dict:
        url = f"{self.server}/api/v1/admin/tile-operations/jobs"
        payload: dict = {"operation": "seed", "serviceId": service_id,
                         "minZoom": minz, "maxZoom": maxz}
        if layer_id is not None:
            payload["layerId"] = layer_id
        if bbox:
            payload["bbox"] = list(bbox)
        args = ["-X", "POST", url, "-H", "Content-Type: application/json",
                "-d", json.dumps(payload)]
        _, out = self._curl(args)
        return _parse_json(out, "tile-operations")


def _parse_json(text: str, where: str) -> dict:
    text = text.strip()
    if not text:
        return {}
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        raise SystemExit(f"{where} returned non-JSON response:\n{text[:500]}")


# --------------------------------------------------------------------------- #
# Main
# --------------------------------------------------------------------------- #
def main() -> int:
    ap = argparse.ArgumentParser(
        description="Honua open-data area-import provisioner (first slice).",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    ap.add_argument("--source", help="catalog source id (e.g. census-tiger)")
    ap.add_argument("--product", help="product id within the source (e.g. county-boundaries)")
    ap.add_argument("--area",
                    help="bbox:minLon,minLat,maxLon,maxLat  OR  geoid:DDDDD")
    ap.add_argument("--server", default=os.environ.get("HONUA_SERVER", "http://localhost:8080"))
    ap.add_argument("--connection-id", default=os.environ.get("HONUA_CONNECTION_ID", "default"),
                    help="admin connection id to publish the layer under")
    ap.add_argument("--service-id", default="default",
                    help="service id for the tiling job")
    ap.add_argument("--schema", default="public", help="target PostGIS schema")
    ap.add_argument("--table", default=None, help="override target table name")
    ap.add_argument("--overwrite", action="store_true", help="overwrite an existing table")
    ap.add_argument("--tile", action="store_true", help="queue a PMTiles tiling job after publish")
    ap.add_argument("--list", action="store_true", help="list catalog sources/products and exit")
    ap.add_argument("--fetch-only", action="store_true",
                    help="stop after fetch+clip; print the local GeoJSON path (no server needed)")
    ap.add_argument("--import-feedstock", action="store_true",
                    help="import a geocoder/router feedstock product as features "
                         "(the build-geocoder/build-router GP-on-Batch job consumes the "
                         "published layer; see docs/guides/open-data-provisioner.md)")
    args = ap.parse_args()

    catalog = load_catalog()

    if args.list:
        for src in catalog["sources"]:
            status = src.get("status", "available")
            print(f"{src['id']}  [{status}]  ({src['license']['spdx']})  {src['name']}")
            for p in src["products"]:
                print(f"    - {p['product']:<22} {p['geometryType']:<16} -> {p.get('sourceProduct','features')}")
        return 0

    if not (args.source and args.product and args.area):
        raise SystemExit("--source, --product and --area are required "
                         "(unless --list). See --help.")

    source = find_source(catalog, args.source)
    product = find_product(source, args.product)
    area = Area.parse(args.area)

    if source.get("status") == "placeholder":
        raise SystemExit(
            f"source '{source['id']}' is a curated PLACEHOLDER -- its extract recipe "
            f"is a future slice. See areaParam.notes in the catalog and "
            f"docs/guides/open-data-provisioner.md."
        )
    if product.get("sourceProduct") in ("geocoder", "router") and not args.import_feedstock:
        kind = product["sourceProduct"]
        job = "build-geocoder" if kind == "geocoder" else "build-router"
        raise SystemExit(
            f"product '{product['product']}' is a {kind} feedstock. This script imports\n"
            f"the feedstock layer; building the {kind} artifact itself runs on GP-on-Batch\n"
            f"as the '{job}' job (ExecutionJobKind.{'GeocoderBuild' if kind == 'geocoder' else 'RouterBuild'}).\n"
            f"  1. import the feedstock features:  re-run with --import-feedstock\n"
            f"  2. build the {kind} artifact for the area on GP-on-Batch:\n"
            f"       submit a {job} execution job (area-parameterized) — see the\n"
            f"       'geocoding and routing on GP-Batch' section of\n"
            f"       docs/guides/open-data-provisioner.md."
        )

    table = args.table or f"od_{source['id'].replace('-', '_')}_{product['product'].replace('-', '_')}"

    print(f"== provisioning {source['id']}/{product['product']} for {args.area} ==")
    print(f"   license: {source['license']['spdx']} ({source['license']['url']})")
    if source["license"].get("attributionRequired"):
        print(f"   ATTRIBUTION REQUIRED on derived layers: {source['license'].get('attributionText')}")

    with tempfile.TemporaryDirectory(prefix="honua-od-") as tmp:
        workdir = Path(tmp)
        print("-- step 1/4: fetch + clip")
        geojson = fetch_and_prepare(product, area, workdir)
        print(f"   prepared: {geojson} ({geojson.stat().st_size:,} bytes)")

        if args.fetch_only:
            kept = Path.cwd() / geojson.name
            shutil.copy(geojson, kept)
            print(f"   --fetch-only: copied to {kept}")
            return 0

        api_key = os.environ.get("HONUA_ADMIN_API_KEY")
        if not api_key:
            raise SystemExit("set HONUA_ADMIN_API_KEY to talk to the server "
                             "(or pass --fetch-only to stop after clipping)")
        honua = Honua(args.server, api_key)

        print("-- step 2/4: import via admin file-import API")
        imp = honua.import_upload(geojson, table, args.overwrite)
        print(f"   import response: {json.dumps(imp)[:400]}")

        print("-- step 3/4: publish as Honua layer")
        pub = honua.publish_layer(args.connection_id, table, product["layerName"],
                                  product["geometryType"], args.schema)
        print(f"   publish response: {json.dumps(pub)[:400]}")
        layer_id = _extract_layer_id(pub)

        if args.tile:
            tile_cfg = product.get("tile", {})
            if not tile_cfg.get("enabled"):
                print("-- step 4/4: tiling SKIPPED (product.tile.enabled is false)")
            else:
                print("-- step 4/4: queue PMTiles tiling job (in-process or GP-Batch)")
                job = honua.tile_job(
                    args.service_id, layer_id,
                    area.bbox if area.kind == "bbox" else None,
                    tile_cfg.get("minZoom", 0), tile_cfg.get("maxZoom", 14),
                )
                print(f"   tile job response: {json.dumps(job)[:400]}")
        else:
            print("-- step 4/4: tiling SKIPPED (pass --tile to enable)")

    print("== done ==")
    return 0


def _extract_layer_id(pub: dict) -> int | None:
    for key in ("layerId", "id"):
        node = pub.get("data", pub)
        if isinstance(node, dict) and key in node and isinstance(node[key], int):
            return node[key]
    return None


if __name__ == "__main__":
    sys.exit(main())
