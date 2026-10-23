# OpenLayers client interop lane. Uses the official Playwright image so the
# headless Chromium matches what tests/js-browser/ expects.

FROM mcr.microsoft.com/playwright:v1.59.1-noble@sha256:b0ab6f3cb99aa7803adbc14d9027ec1785fc6e433b97e134e0f8fe61683b6b53

ENV DEBIAN_FRONTEND=noninteractive \
    NODE_OPTIONS="--max-old-space-size=4096" \
    HONUA_BASE_URL="http://honua:5000" \
    CI="1"

RUN apt-get update \
 && apt-get install -y --no-install-recommends curl jq \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /workspace

COPY docker/client-compat/openlayers/run.sh /usr/local/bin/run-openlayers-lane
RUN chmod +x /usr/local/bin/run-openlayers-lane

ENTRYPOINT ["/usr/local/bin/run-openlayers-lane"]
