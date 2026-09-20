FROM ghcr.io/osgeo/gdal:ubuntu-small-3.8.4@sha256:60d3bc2f8b09ca1a7ef2db0239699b2c03713aa02be6e525e731c0020bbb10a4
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3-pip \
    && python3 -m pip install --break-system-packages OWSLib==0.36.0 \
    && rm -rf /var/lib/apt/lists/*
