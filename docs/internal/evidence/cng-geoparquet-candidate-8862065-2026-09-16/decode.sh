#!/usr/bin/env bash
# honua-server#4747 criterion 3: read the GeoParquet the pinned AOT image served
# with four independent consumers, and assert it against the seeded fixture.
#
# Every consumer runs in its own pinned container, so nothing depends on what this
# host happens to have installed:
#   gpq v0.24.0        the CNG lane's GeoParquet conformance validator
#   PyArrow 25.0.1     raw Parquet + a hand-written WKB reader (verify-geoparquet.py)
#   GeoPandas 1.1.4    Shapely geometry decode (verify-geoparquet.py)
#   GDAL (lane digest) ogrinfo -al full feature read-back
#
# usage: decode.sh <dir-with-cng.parquet>
set -euo pipefail

DIR="$(cd "${1:?directory containing cng.parquet required}" && pwd)"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GPQ_VERSION="v0.24.0"
# Same digest the CNG lane pins in .github/workflows/cng-conformance.yml.
GDAL_IMAGE="ghcr.io/osgeo/gdal@sha256:323828a57fd01e2f0a96ece1b2caf6b4ad41e2e47458386836697418fd67665c"

echo "== gpq ${GPQ_VERSION} validate =="
docker run --rm -v "$DIR:/data:ro" golang:1.25 sh -c \
  "go install github.com/planetlabs/gpq/cmd/gpq@${GPQ_VERSION} >/dev/null 2>&1 && \
   /go/bin/gpq describe /data/cng.parquet && /go/bin/gpq validate /data/cng.parquet" \
  2>&1 | tee "$DIR/gpq.log"

echo "== PyArrow 25.0.1 + GeoPandas 1.1.4 fixture assertion =="
docker run --rm -v "$DIR:/data" -v "$HERE/verify-geoparquet.py:/verify.py:ro" python:3.12-slim sh -c \
  "pip install -q pyarrow==25.0.1 geopandas==1.1.4 >/dev/null && \
   python /verify.py /data/cng.parquet /data/fixture-assertion.json" \
  2>&1 | tee "$DIR/pyarrow-geopandas.log"

echo "== GDAL ogrinfo -al read-back =="
docker run --rm -v "$DIR:/data:ro" "$GDAL_IMAGE" \
  ogrinfo -al /data/cng.parquet 2>&1 | tee "$DIR/ogrinfo.log"

echo
echo "all four consumers decoded $DIR/cng.parquet"
