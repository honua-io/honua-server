# OWSLib canonical OGC client interop lane (#3392, parent #3389).
#
# OWSLib is the reference Python client for the classic OGC service stack
# (WMS/WMTS/WFS) and for OGC API - Features. It parses capabilities documents
# strictly, so this lane is the single best chance the certification matrix has
# of catching capabilities-document bugs before a real analyst does.
#
# The image carries no GIS runtime at all: OWSLib speaks HTTP + XML/JSON, and
# Pillow is only here so the WMS/WMTS lanes can decode the server-rendered
# imagery instead of trusting a Content-Type header.
#
# Compose wiring (docker/client-compat/compose.yml, `owslib` service) must
# mount:
#   - ../../tests:/workspace/tests:ro     (the suite + shared envelope writer)
#   - ./output/owslib:/output             (where the .cert.json envelopes land)
# and set HONUA_BASE_URL to the compose `honua` service.

FROM python:3.12-slim-bookworm@sha256:782412e85d0f0984994c290652577d4018aff08145c85b262bb63dc0c7522254

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PIP_NO_CACHE_DIR=1

# curl backs the readiness wait in run.sh. ca-certificates keeps requests happy
# when the lane is pointed at an HTTPS candidate in the release tier.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /workspace

COPY docker/client-compat/owslib/requirements.txt /tmp/requirements.txt
RUN pip install --no-cache-dir -r /tmp/requirements.txt

COPY docker/client-compat/owslib/run.sh /usr/local/bin/run-owslib-lane
RUN chmod +x /usr/local/bin/run-owslib-lane

ENTRYPOINT ["/usr/local/bin/run-owslib-lane"]
