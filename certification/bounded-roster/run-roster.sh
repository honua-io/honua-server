#!/usr/bin/env bash
# Run the bounded 2026.1 external-client roster (honua-server#3434) against one
# immutable imaged candidate and write governed receipts plus the release-mode
# verdict.
#
#   certification/bounded-roster/run-roster.sh \
#       --tag nightly-2cc2213 \
#       --expect-digest sha256:61e06ef3a94d00e4c8fc57ce93e008a5e31b2dcf1da5deb22781fdd42d2d4e51 \
#       --evidence docs/internal/evidence/client-certification-2cc2213 \
#       --run-root /path/on/real/disk/roster-runs
#
# The producer is this repository at a clean, committed HEAD: the receipts'
# producer_source_sha is that commit, so uncommitted harness edits refuse to run.
# Only the compose project named by --project is created or torn down.
set -euo pipefail

TAG="" EXPECT_DIGEST="" EVIDENCE="" RUN_ROOT="" PROJECT="roster3434" KEEP_STACK=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --tag) TAG="$2"; shift 2 ;;
    --expect-digest) EXPECT_DIGEST="$2"; shift 2 ;;
    --evidence) EVIDENCE="$2"; shift 2 ;;
    --run-root) RUN_ROOT="$2"; shift 2 ;;
    --project) PROJECT="$2"; shift 2 ;;
    --keep-stack) KEEP_STACK=1; shift ;;
    *) echo "unknown argument $1" >&2; exit 2 ;;
  esac
done
[[ -n "$TAG" && -n "$EVIDENCE" && -n "$RUN_ROOT" ]] || { echo "--tag, --evidence and --run-root are required" >&2; exit 2; }

REPO_ROOT="$(git rev-parse --show-toplevel)"
ROSTER_HOME="$REPO_ROOT/certification/bounded-roster"
cd "$REPO_ROOT"

if ! git diff --quiet HEAD -- certification tests/python/shared tests/seed docker/client-compat docker/cng; then
  echo "producer inputs have uncommitted changes; commit them so producer_source_sha names the harness that ran" >&2
  exit 2
fi
PRODUCER_SHA="$(git rev-parse HEAD)"

log() { printf '[%s] %s\n' "$(date -u +%H:%M:%SZ)" "$*"; }
retry() { local attempt; for attempt in 1 2 3 4 5; do "$@" && return 0; sleep $((attempt * 10)); done; return 1; }

# --- candidate ---------------------------------------------------------------
IMAGE_REPO="ghcr.io/honua-io/honua-server"
ANON_DOCKER_CONFIG="$(mktemp -d)"; echo '{}' > "$ANON_DOCKER_CONFIG/config.json"
log "pulling $IMAGE_REPO:$TAG"
retry env DOCKER_CONFIG="$ANON_DOCKER_CONFIG" docker pull -q "$IMAGE_REPO:$TAG" >/dev/null
INDEX_DIGEST="$(docker image inspect "$IMAGE_REPO:$TAG" --format '{{range .RepoDigests}}{{println .}}{{end}}' | sed -n "s#^$IMAGE_REPO@##p" | head -1)"
[[ "$INDEX_DIGEST" =~ ^sha256:[0-9a-f]{64}$ ]] || { echo "could not resolve an index digest for $TAG" >&2; exit 1; }
if [[ -n "$EXPECT_DIGEST" && "$INDEX_DIGEST" != "$EXPECT_DIGEST" ]]; then
  echo "$TAG resolves to $INDEX_DIGEST, not the expected $EXPECT_DIGEST" >&2; exit 1
fi
SOURCE_SHA="$(docker image inspect "$IMAGE_REPO:$TAG" --format '{{index .Config.Labels "org.opencontainers.image.revision"}}')"
IMAGE_CREATED="$(docker image inspect "$IMAGE_REPO:$TAG" --format '{{index .Config.Labels "org.opencontainers.image.created"}}')"
PLATFORM_ID="$(docker image inspect "$IMAGE_REPO:$TAG" --format '{{.Id}}')"
[[ "$SOURCE_SHA" =~ ^[0-9a-f]{40}$ ]] || { echo "image carries no source revision label" >&2; exit 1; }
export HONUA_CANDIDATE_IMAGE="$IMAGE_REPO@$INDEX_DIGEST"

RUN_ID="roster-$(date -u +%Y%m%dT%H%M%SZ)-${SOURCE_SHA:0:7}"
export ROSTER_RUN_DIR="$RUN_ROOT/$RUN_ID"
export ROSTER_HOME ROSTER_REPO_ROOT="$REPO_ROOT"
mkdir -p "$ROSTER_RUN_DIR"/{wire,observations,assets}
chmod -R a+rwX "$ROSTER_RUN_DIR"
log "run $RUN_ID candidate $SOURCE_SHA $INDEX_DIGEST producer $PRODUCER_SHA"

# The fixture auth profile lives in the client-compat `honua` service definition.
BASE_ENV="$(docker compose -f docker/client-compat/compose.yml config --format json | jq '.services.honua.environment')"
export ROSTER_FIXTURE_ADMIN_KEY="$(jq -r '.HONUA_ADMIN_PASSWORD' <<<"$BASE_ENV")"
export ROSTER_FIXTURE_OIDC_ISSUER="$(jq -r '.Oidc__Generic__Authority' <<<"$BASE_ENV")"
export ROSTER_FIXTURE_OIDC_AUDIENCE="$(jq -r '.Oidc__Generic__ClientId' <<<"$BASE_ENV")"
export ROSTER_FIXTURE_OIDC_SIGNING_KEY="$(jq -r '.Oidc__TokenValidation__SymmetricSigningKey' <<<"$BASE_ENV")"
for value in "$ROSTER_FIXTURE_ADMIN_KEY" "$ROSTER_FIXTURE_OIDC_ISSUER" "$ROSTER_FIXTURE_OIDC_AUDIENCE" "$ROSTER_FIXTURE_OIDC_SIGNING_KEY"; do
  [[ -n "$value" && "$value" != null ]] || { echo "could not read the fixture auth profile from docker/client-compat/compose.yml" >&2; exit 1; }
done

COMPOSE=(docker compose -p "$PROJECT" -f docker/client-compat/compose.yml -f "$ROSTER_HOME/compose.candidate.yml")
teardown() { if [[ "$KEEP_STACK" != 1 ]]; then "${COMPOSE[@]}" --profile roster --profile multidim-fixture down -v --remove-orphans >/dev/null 2>&1 || true; fi; }
trap teardown EXIT
"${COMPOSE[@]}" --profile roster --profile multidim-fixture down -v --remove-orphans >/dev/null 2>&1 || true

# --- stack -----------------------------------------------------------------
log "starting postgres, redis, localstack"
"${COMPOSE[@]}" up -d --wait postgres redis localstack
log "starting the candidate"
if ! "${COMPOSE[@]}" up -d --wait --wait-timeout 420 honua; then
  # A migration that raced PostgreSQL's first accept is retried once, before any seed exists.
  log "candidate not ready; restarting once before seeding"
  docker restart "${PROJECT}-honua-1" >/dev/null
  for _ in $(seq 1 42); do
    [[ "$(docker inspect "${PROJECT}-honua-1" --format '{{.State.Health.Status}}')" == healthy ]] && break
    sleep 10
  done
  [[ "$(docker inspect "${PROJECT}-honua-1" --format '{{.State.Health.Status}}')" == healthy ]] || { echo "candidate never became ready" >&2; exit 1; }
fi
docker run --rm --network "${PROJECT}_compat" -e KEY="$ROSTER_FIXTURE_ADMIN_KEY" curlimages/curl:8.10.1 \
  sh -c 'curl -s -m 30 -H "X-API-Key: $KEY" http://honua:5000/api/v1/admin/version' > "$ROSTER_RUN_DIR/server-version.json" || true

log "seeding the client-compat fixture"
"${COMPOSE[@]}" run --rm seed >"$ROSTER_RUN_DIR/seed.log" 2>&1
PSQL=(docker exec -i "${PROJECT}-postgres-1" psql -U postgres -d honua_compat -v ON_ERROR_STOP=1 -q)
log "applying docker/cng/seed.sql and the roster raster fixture"
"${PSQL[@]}" < docker/cng/seed.sql >>"$ROSTER_RUN_DIR/seed.log" 2>&1
echo "SELECT honua.seed_metadata_v2_compat_snapshot();" | "${PSQL[@]}" >>"$ROSTER_RUN_DIR/seed.log" 2>&1
"${PSQL[@]}" < "$ROSTER_HOME/fixture/roster-raster-fixture.sql" >>"$ROSTER_RUN_DIR/seed.log" 2>&1
for _ in $(seq 1 30); do
  code="$(docker run --rm --network "${PROJECT}_compat" curlimages/curl:8.10.1 -s -o /dev/null -w '%{http_code}' -m 10 http://honua:5000/terrain/5100/tile.json || true)"
  [[ "$code" == 200 ]] && break
  sleep 5
done

log "building lane images"
"${COMPOSE[@]}" --profile roster build lane-python lane-gdal-3.8.4 lane-gdal-3.13.3 lane-qgis lane-maplibre >"$ROSTER_RUN_DIR/build.log" 2>&1

log "authoring and registering cloud raster fixtures"
docker run --rm --network "${PROJECT}_compat" --user "$(id -u):$(id -g)" -e HOME=/tmp -w /tmp \
  -e HONUA_CERT_API_KEY="$ROSTER_FIXTURE_ADMIN_KEY" -e ROSTER_FIXTURE_OUTPUT=/run/fixture-artifacts.json \
  -v "$ROSTER_HOME/fixture:/roster/fixture:ro" -v "$ROSTER_RUN_DIR:/run" \
  honua-roster/lane-gdal:3.13.3 python3 /roster/fixture/apply_fixture.py

cid="$(docker create honua-roster/lane-maplibre:local)"
docker cp "$cid:/opt/roster-assets/." "$ROSTER_RUN_DIR/assets/" >/dev/null
docker rm "$cid" >/dev/null
cp "$ROSTER_HOME/lanes/maplibre/page/index.html" "$ROSTER_RUN_DIR/assets/index.html"
chmod -R a+rwX "$ROSTER_RUN_DIR" 2>/dev/null || true

log "starting the recording proxy"
"${COMPOSE[@]}" --profile roster up -d wire
sleep 3

run_lane() {
  local service="$1" lane="$2" identity="$3"; shift 3
  log "lane $lane ($service): $*"
  "${COMPOSE[@]}" --profile roster run --rm -e ROSTER_LANE_IDENTITY_COMMAND="$identity" "$service" \
    sh -c "cd /roster && exec \$(command -v python3 || command -v python) /roster/lib/runlane.py $lane $*" \
    >"$ROSTER_RUN_DIR/lane-$lane.log" 2>&1 || log "lane $lane reported harness failures (see lane-$lane.log)"
}
run_lane lane-python python "pip freeze" owslib_cells pystac_cells
run_lane lane-gdal-3.8.4 gdal-3.8.4 "gdalinfo --version" gdal_ogc_cells gdal_cng_cells
run_lane lane-gdal-3.13.3 gdal-3.13.3 "gdalinfo --version" gdal_cng_cells
run_lane lane-qgis qgis "python3 -c 'from qgis.core import Qgis; print(Qgis.version())'" qgis_cells
run_lane lane-maplibre maplibre "cat /opt/roster-assets/versions.txt" maplibre_cells

# --- identities, receipts, verdict ------------------------------------------
log "recording candidate, fixture and lane identities"
python3 - "$ROSTER_RUN_DIR" <<PY
import hashlib, json, subprocess, sys
from pathlib import Path
run = Path(sys.argv[1])
def sha(path): return hashlib.sha256(Path(path).read_bytes()).hexdigest()
def inspect(image, template):
    return subprocess.run(["docker", "image", "inspect", image, "--format", template], capture_output=True, text=True).stdout.strip()
candidate = {
    "run_id": "$RUN_ID", "release": "2026.1", "cut": "pre-cut proof on the imaged trunk nightly (operator ruling A, 2026-09-16); no release cut exists",
    "tag": "$TAG", "image": "$HONUA_CANDIDATE_IMAGE", "index_digest": "$INDEX_DIGEST", "platform_image_id": "$PLATFORM_ID",
    "source_sha": "$SOURCE_SHA", "image_created": "$IMAGE_CREATED",
    "producer_source_sha": "$PRODUCER_SHA", "target": "local-docker",
    "durable_uri_base": "https://github.com/honua-io/honua-server/blob/trunk/$EVIDENCE/receipts/",
}
try:
    candidate["server_version"] = json.loads((run / "server-version.json").read_text())
except Exception:
    candidate["server_version"] = None
seeds = ["tests/seed/client-compat-v1.sql", "tests/seed/browser-compat.yaml", "tests/seed/client-compat-auth-wave1.yaml",
         "tests/seed/portal-compat.yaml", "docker/cng/seed.sql", "certification/bounded-roster/fixture/roster-raster-fixture.sql",
         "certification/bounded-roster/fixture/apply_fixture.py"]
config = ["docker/client-compat/compose.yml", "certification/bounded-roster/compose.candidate.yml"]
auth = ["tests/python/shared/cert_auth.py", "certification/bounded-roster/lib/rosterenv.py"]
def digest_set(paths):
    files = {path: sha(path) for path in paths}
    return {"files": files, "sha256": hashlib.sha256("".join(f"{p}={d}\n" for p, d in sorted(files.items())).encode()).hexdigest()}
artifacts = json.loads((run / "fixture-artifacts.json").read_text())
fixture = {"fixture": digest_set(seeds), "server_config": digest_set(config), "auth_profile": digest_set(auth),
           "authored_objects": {"cog_sha256": artifacts["cog"]["sha256"], "zarr_objects": artifacts["zarr"]["objects"]},
           "registrations": artifacts["registrations"]}
lanes = {"lanes": {}, "by_client_lane": {}}
for lane, image, base in (("python", "honua-roster/lane-python:local", "python:3.12-slim-bookworm"),
                          ("gdal-3.8.4", "honua-roster/lane-gdal:3.8.4", "ghcr.io/osgeo/gdal:ubuntu-small-3.8.4"),
                          ("gdal-3.13.3", "honua-roster/lane-gdal:3.13.3", "ghcr.io/osgeo/gdal:ubuntu-full-3.13.3"),
                          ("qgis", "honua-roster/lane-qgis:3.44.13", "qgis/qgis:3.44.13"),
                          ("maplibre", "honua-roster/lane-maplibre:local", "mcr.microsoft.com/playwright:v1.59.1-noble")):
    lanes["lanes"][lane] = {"lane": lane, "image": image, "image_id": inspect(image, "{{.Id}}"),
                            "base_image": base, "base_repo_digests": inspect(base, "{{json .RepoDigests}}")}
for client_lane, lane in (("py-owslib", "python"), ("py-pystac", "python"), ("desktop-qgis", "qgis"), ("js-maplibre", "maplibre"),
                          ("gdal-cog", "gdal-3.8.4"), ("gdal-flatgeobuf", "gdal-3.8.4"), ("gdal-geoparquet", "gdal-3.13.3")):
    lanes["by_client_lane"][client_lane] = lanes["lanes"].get(lane)
lanes["by_client_lane"]["gdal"] = {"gdal 3.8.4": lanes["lanes"]["gdal-3.8.4"], "gdal 3.13.3": lanes["lanes"]["gdal-3.13.3"]}
(run / "candidate.json").write_text(json.dumps(candidate, indent=2) + "\n")
(run / "fixture.json").write_text(json.dumps(fixture, indent=2) + "\n")
(run / "lanes.json").write_text(json.dumps(lanes, indent=2) + "\n")
PY

log "emitting receipts"
mkdir -p "$EVIDENCE"
python3 "$ROSTER_HOME/lib/emit_receipts.py" --run-dir "$ROSTER_RUN_DIR" \
  --requirements certification/client-protocol-requirements.v1.json \
  --producer-source-sha "$PRODUCER_SHA" --out "$EVIDENCE/receipts"

log "release-mode verdict"
set +e
python3 scripts/certification/verify-client-certification-receipts.py --mode release \
  --receipts "$EVIDENCE/receipts" --source-sha "$SOURCE_SHA" --image-digest "$INDEX_DIGEST" \
  --cut-at "$IMAGE_CREATED" --producer-source-sha "$PRODUCER_SHA" \
  --output "$EVIDENCE/verdict.release.json" | tee "$EVIDENCE/verdict.release.txt"
python3 scripts/certification/verify-client-certification-receipts.py \
  --output "$EVIDENCE/verdict.contract.json" > "$EVIDENCE/verdict.contract.txt"
set -e

mkdir -p "$EVIDENCE/run"
cp "$ROSTER_RUN_DIR"/{candidate.json,fixture.json,lanes.json,fixture-artifacts.json} "$EVIDENCE/run/"
cp -r "$ROSTER_RUN_DIR/observations" "$EVIDENCE/run/"
gzip -9 -c "$ROSTER_RUN_DIR/wire/wire.jsonl" > "$EVIDENCE/run/wire.jsonl.gz"
sha256sum "$ROSTER_RUN_DIR/wire/wire.jsonl" | awk '{print $1 "  wire.jsonl"}' > "$EVIDENCE/run/wire.jsonl.sha256"
log "done: $EVIDENCE (raw run directory $ROSTER_RUN_DIR)"
