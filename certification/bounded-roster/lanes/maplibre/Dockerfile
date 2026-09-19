# MapLibre GL JS client lane (js-maplibre): pinned MapLibre releases driven in the
# Playwright Chromium build, from Python so the lane shares cellkit with the others.
FROM mcr.microsoft.com/playwright:v1.59.1-noble
ENV DEBIAN_FRONTEND=noninteractive PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1 PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN apt-get update \
 && apt-get install -y --no-install-recommends python3-venv \
 && rm -rf /var/lib/apt/lists/* \
 && python3 -m venv /opt/venv \
 && /opt/venv/bin/pip install --no-cache-dir playwright==1.59.0 \
 && /opt/venv/bin/pip freeze > /opt/lane-freeze.txt
ENV PATH=/opt/venv/bin:${PATH}
# Governed MapLibre releases: "5.7" (resolved to the newest 5.7.x) and "6.5.0".
WORKDIR /opt/maplibre
RUN npm init -y >/dev/null \
 && npm install --no-audit --no-fund maplibre-5.7@npm:maplibre-gl@5.7.3 maplibre-6.5.0@npm:maplibre-gl@6.5.0 \
 && mkdir -p /opt/roster-assets \
 && cp -r node_modules/maplibre-5.7/dist /opt/roster-assets/maplibre-5.7 \
 && cp -r node_modules/maplibre-6.5.0/dist /opt/roster-assets/maplibre-6.5.0 \
 && node -e "for (const p of ['maplibre-5.7','maplibre-6.5.0']) console.log(p, require('./node_modules/'+p+'/package.json').version)" > /opt/roster-assets/versions.txt \
 && (cd /opt/roster-assets && find maplibre-5.7 maplibre-6.5.0 -maxdepth 1 -type f | sort | xargs sha256sum) >> /opt/roster-assets/versions.txt
WORKDIR /roster
