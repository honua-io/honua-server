# GDAL/OGR client interop lane. Uses the upstream osgeo image so the OGR
# WFS, OAPIF, and CSV drivers match what production clients ship.

FROM ghcr.io/osgeo/gdal:ubuntu-small-latest

ENV PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PIP_NO_CACHE_DIR=1

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        python3-pip \
        python3-venv \
        ca-certificates \
        curl \
        jq \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /workspace

# Install Python test dependencies into a venv to avoid clashing with the
# distro-managed python3.
RUN python3 -m venv /opt/venv
ENV PATH="/opt/venv/bin:${PATH}"

COPY tests/python/requirements.txt /tmp/requirements.txt
RUN pip install -r /tmp/requirements.txt

# The compose file mounts tests/ read-only and /output read-write at runtime;
# the entrypoint script just runs the gdal_ogr suite and copies the JSON
# evidence to the shared output volume.
COPY docker/client-compat/gdal/run.sh /usr/local/bin/run-gdal-lane
RUN chmod +x /usr/local/bin/run-gdal-lane

ENTRYPOINT ["/usr/local/bin/run-gdal-lane"]
