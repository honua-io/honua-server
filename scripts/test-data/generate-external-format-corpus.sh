#!/usr/bin/env bash
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
#
# Regenerates tests/fixtures/external-format-corpus/v1 — the format fixtures that Honua did
# NOT author (honua-server#4419). Every binary in that corpus is written by GDAL/OGR, an
# independent implementation, from the three checked-in `*.source.geojson` files. That is the
# whole point: a fixture serialized by the same library the reader under test consumes cannot
# detect a shared misinterpretation (a transposed coordinate order, a mis-signed ordinate, a
# dropped ring), and most format fixtures in this repository were generated that way.
#
# Requires Docker. Usage:  scripts/test-data/generate-external-format-corpus.sh
set -euo pipefail

IMAGE="${HONUA_GDAL_IMAGE:-ghcr.io/osgeo/gdal:ubuntu-small-latest}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CORPUS="$ROOT/tests/fixtures/external-format-corpus/v1"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

cp "$CORPUS"/*.source.geojson "$WORK/"
mkdir -p "$WORK/out"

ogr() {
  docker run --rm -u "$(id -u):$(id -g)" -v "$WORK:/w" -w /w "$IMAGE" ogr2ogr "$@"
}

ogr -f OpenFileGDB    out/survey_sites.gdb        survey-sites.source.geojson       -nln survey_sites -a_srs EPSG:4326
ogr -f KML            out/survey-sites.kml        survey-sites.source.geojson       -nln survey_sites
ogr -f KML            out/polygon-with-hole.kml   polygon-with-hole.source.geojson  -nln zones
ogr -f KML            out/routes.kml              routes.source.geojson             -nln routes
ogr -f FlatGeobuf     out/survey-sites.fgb        survey-sites.source.geojson       -nln survey_sites -a_srs EPSG:4326
ogr -f GPKG           out/survey-sites.gpkg       survey-sites.source.geojson       -nln survey_sites -a_srs EPSG:4326
ogr -f GPKG           out/polygon-with-hole.gpkg  polygon-with-hole.source.geojson  -nln zones        -a_srs EPSG:4326
ogr -f GPX            out/survey-sites.gpx        survey-sites.source.geojson       -nln waypoints    -dsco GPX_USE_EXTENSIONS=YES
ogr -f CSV            out/survey-sites.csv        survey-sites.source.geojson       -lco GEOMETRY=AS_WKT
ogr -f "ESRI Shapefile" out/survey_sites_shp      survey-sites.source.geojson       -nln survey_sites -a_srs EPSG:4326 -lco ENCODING=UTF-8
ogr -f "ESRI Shapefile" out/zones_shp             polygon-with-hole.source.geojson  -nln zones        -a_srs EPSG:4326 -lco ENCODING=UTF-8

# Deterministic ZIPs: fixed timestamps and sorted entries, so a regeneration that changed no
# bytes produces no diff.
python3 - "$WORK/out" "$CORPUS" <<'PY'
import hashlib, json, os, pathlib, shutil, sys, zipfile

out, corpus = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])

def zipdir(src, dest, prefix=""):
    with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(p for p in src.rglob("*") if p.is_file()):
            info = zipfile.ZipInfo(os.path.join(prefix, str(path.relative_to(src))), (2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            archive.writestr(info, path.read_bytes())

zipdir(out / "survey_sites.gdb", corpus / "survey-sites.gdb.zip", "survey_sites.gdb")
zipdir(out / "survey_sites_shp", corpus / "survey-sites-shapefile.zip")
zipdir(out / "zones_shp", corpus / "polygon-with-hole-shapefile.zip")
for name in ("survey-sites.kml", "polygon-with-hole.kml", "routes.kml", "survey-sites.fgb",
             "survey-sites.gpkg", "polygon-with-hole.gpkg", "survey-sites.gpx", "survey-sites.csv"):
    shutil.copyfile(out / name, corpus / name)

manifest = json.loads((corpus / "manifest.json").read_text(encoding="utf-8"))
for asset in manifest["assets"]:
    blob = (corpus / asset["path"]).read_bytes()
    asset["byteLength"] = len(blob)
    asset["sha256"] = hashlib.sha256(blob).hexdigest()
(corpus / "manifest.json").write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print(f"refreshed {len(manifest['assets'])} manifest entries")
PY

docker run --rm "$IMAGE" ogrinfo --version
echo "Record the GDAL version above in the corpus manifest's provenance block."
