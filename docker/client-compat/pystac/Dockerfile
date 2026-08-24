# pystac-client client interop lane (honua-server#3392).
#
# Certifies the Honua STAC surface against the canonical Python STAC analyst
# stack (`pystac` + `pystac-client`) and emits the `py-pystac` / `stac`
# certification envelope.
#
# Plain python:3.12-slim rather than a GIS base image: this lane is a
# catalog/metadata client, so it needs no GDAL/PROJ/QGIS runtime. It also never
# starts a database or a server — it targets the compose `honua` service via
# HONUA_BASE_URL.
#
# Compose must provide two mounts (see docker/client-compat/compose.yml):
#   ../../tests:/workspace/tests:ro   - the suite; read-only, so nothing is
#                                       ever written back into the repo
#   ./output/pystac:/output           - where the .cert.json envelope lands

FROM python:3.12-slim-bookworm

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PIP_NO_CACHE_DIR=1

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /workspace

# Lane-local pins (not tests/python/requirements.txt): this lane must not pull
# the local-server test stack it never uses. See the requirements file header.
COPY docker/client-compat/pystac/requirements.txt /tmp/requirements.txt
RUN pip install --no-cache-dir -r /tmp/requirements.txt

COPY docker/client-compat/pystac/run.sh /usr/local/bin/run-pystac-lane
RUN chmod +x /usr/local/bin/run-pystac-lane

ENTRYPOINT ["/usr/local/bin/run-pystac-lane"]
