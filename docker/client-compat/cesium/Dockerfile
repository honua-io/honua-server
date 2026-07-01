# Cesium client interop lane. Uses the official Playwright image so headless
# Chromium with WebGL works without further configuration.
#
# IMPORTANT: keep this image tag in lockstep with the @playwright/test version
# resolved in tests/js-browser/cesium/package-lock.json. The image bakes in the
# matching browser build; a drift (e.g. the lockfile bumped to 1.61.0 while this
# stayed at 1.59.1) makes `npx playwright test` fail with
# "browserType.launch: Executable doesn't exist at .../chromium_headless_shell-<build>".
FROM mcr.microsoft.com/playwright:v1.61.0-noble

ENV DEBIAN_FRONTEND=noninteractive \
    NODE_OPTIONS="--max-old-space-size=4096" \
    HONUA_BASE_URL="http://honua:5000" \
    CI="1"

RUN apt-get update \
 && apt-get install -y --no-install-recommends curl jq \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /workspace

COPY docker/client-compat/cesium/run.sh /usr/local/bin/run-cesium-lane
RUN chmod +x /usr/local/bin/run-cesium-lane

ENTRYPOINT ["/usr/local/bin/run-cesium-lane"]
