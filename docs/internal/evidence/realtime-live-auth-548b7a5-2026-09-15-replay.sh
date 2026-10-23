#!/usr/bin/env bash
# Live replay of the remaining server#4776 / #4777 / #4778 criteria on the pinned
# 2026.1 candidate (548b7a5, AOT index sha256:29974ee7...).
#
# Boots the candidate through honua-sdk-js's unmodified
# scripts/realtime-live-candidate-deployment.sh (PostGIS + Redis, the candidate's
# own client-compat seed, the two-tenant overlay, Staging with the image's
# production settings and default MultiTenancy configuration), then runs
# realtime-live-auth-548b7a5-2026-09-15-probe.mjs, which records every boundary
# response and close frame without any credential. Tears down on exit.
#
#   HONUA_SDK_JS_CHECKOUT=<honua-sdk-js checkout with node_modules>
#   HONUA_REPLAY_OUTPUT=<transcript json path>
#   HONUA_SERVER_IMAGE (default: the pinned digest), HONUA_SERVER_REVISION,
#   HONUA_REPLAY_PORT (default 18190)
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
: "${HONUA_SDK_JS_CHECKOUT:?set to a honua-sdk-js checkout with node_modules installed}"
: "${HONUA_REPLAY_OUTPUT:?set to the transcript output path}"
image="${HONUA_SERVER_IMAGE:-ghcr.io/honua-io/honua-server@sha256:29974ee7b722e3ae15c3b891024e5e70800f412188aeccf5ec3d32d9dac675c1}"
revision="${HONUA_SERVER_REVISION:-548b7a5263da5a3f2381eb43f232687cdf92b0bf}"
port="${HONUA_REPLAY_PORT:-18190}"
deploy="$HONUA_SDK_JS_CHECKOUT/scripts/realtime-live-candidate-deployment.sh"

export HONUA_REALTIME_CANDIDATE_NETWORK="honua-live-auth-replay-548b7a5"
export HONUA_REALTIME_CANDIDATE_PORT="$port"
export HONUA_REALTIME_CANDIDATE_IMAGE="$image"
export HONUA_REALTIME_CANDIDATE_REVISION="$revision"
export HONUA_REALTIME_CANDIDATE_ADMIN_API_KEY="Replay-$(openssl rand -hex 12)!"
export HONUA_REALTIME_ISSUER="https://issuer.realtime-conformance.invalid"
export HONUA_REALTIME_ISSUER_AUDIENCE="honua-realtime-conformance"
export HONUA_REALTIME_ISSUER_SIGNING_KEY="$(openssl rand -hex 32)"
export HONUA_REALTIME_ISSUER_REFERER="https://realtime-conformance.invalid/"
work="$(mktemp -d "${HONUA_REPLAY_WORK:-$PWD}/live-auth-replay.XXXXXX")"
export HONUA_REALTIME_CANDIDATE_DESCRIPTOR="$work/deployment.json"
export RUNNER_TEMP="$work"

cleanup() {
  "$deploy" logs > "$work/server.log" 2>&1 || true
  "$deploy" down || true
}
trap cleanup EXIT

# The public GHCR image pulls anonymously; a host credential helper can fail it.
if [ -n "${HONUA_REPLAY_ANONYMOUS_PULL:-}" ]; then
  mkdir -p "$work/docker" && echo '{}' > "$work/docker/config.json"
  export DOCKER_CONFIG="$work/docker"
fi
"$deploy" up
export HONUA_REALTIME_CANDIDATE_BASE_URL="http://127.0.0.1:${port}"
export HONUA_REPLAY_DESCRIPTOR="$HONUA_REALTIME_CANDIDATE_DESCRIPTOR"
NODE_PATH="$HONUA_SDK_JS_CHECKOUT/node_modules" node "$here/realtime-live-auth-548b7a5-2026-09-15-probe.mjs" "$HONUA_REPLAY_OUTPUT"
