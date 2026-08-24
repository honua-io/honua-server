# GeoPandas canonical-analyst client interop lane (honua-server#3392).
#
# Unlike the gdal lane (which uses the osgeo GDAL image) this lane deliberately
# starts from a plain python base and installs the wheels an analyst would get
# from `pip install geopandas pyogrio fiona`. The point of the lane is to
# certify the server against the client stack real analysts run - vendored GDAL
# and all.
#
# docker/client-compat/compose.yml must mount:
#     ../../tests:/workspace/tests:ro
#     ./output/geopandas:/output

FROM python:3.12-slim-bookworm

ENV PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_ROOT_USER_ACTION=ignore

# libexpat1 is a runtime dependency of the GDAL vendored inside the fiona
# wheel; without it `import fiona` fails with a bare ImportError on
# libexpat.so.1. curl backs the readiness wait in run.sh.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        libexpat1 \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /workspace

COPY docker/client-compat/geopandas/requirements.txt /tmp/requirements.txt
RUN pip install --no-cache-dir -r /tmp/requirements.txt

COPY docker/client-compat/geopandas/run.sh /usr/local/bin/run-geopandas-lane
RUN chmod +x /usr/local/bin/run-geopandas-lane

ENTRYPOINT ["/usr/local/bin/run-geopandas-lane"]
