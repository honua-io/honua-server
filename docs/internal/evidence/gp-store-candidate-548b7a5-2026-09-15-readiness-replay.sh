#!/usr/bin/env bash
# #4805 readiness replay on the pinned candidate. It repeats the #4770 marker-only
# reproduction (fresh topology, remove only the store marker, six anonymous
# /healthz/ready probes one second apart) and adds the corrupted-marker cases.
# Anonymous probes are the ones the output cache used to replay, so the cache is
# warmed with ready answers before every disruption.
#
# Usage: HONUA_SERVER_IMAGE=ghcr.io/honua-io/honua-server@sha256:<digest> \
#        HONUA_GP_OBJECT_ROOT=<real-disk dir> readiness-replay.sh > log
set -uo pipefail

repo_root="$(git rev-parse --show-toplevel)"
compose_file="${repo_root}/docker/gp-reliability/compose.yml"
: "${HONUA_SERVER_IMAGE:?set HONUA_SERVER_IMAGE to a digest-addressed image}"
: "${HONUA_GP_OBJECT_ROOT:?set HONUA_GP_OBJECT_ROOT to a real-disk directory}"
export HONUA_GP_PORT="${HONUA_GP_PORT:-18180}" HONUA_GP_PEER_PORT="${HONUA_GP_PEER_PORT:-18181}"
# The worker is not started; compose still interpolates its required image.
export HONUA_WORKER_IMAGE="${HONUA_WORKER_IMAGE:-${HONUA_SERVER_IMAGE}}" HONUA_GP_OBJECT_ROOT
project="honua-gp-readiness-4805-$$"
marker="${HONUA_GP_OBJECT_ROOT}/.honua-gp-store.json"
urls=("http://127.0.0.1:${HONUA_GP_PORT}" "http://127.0.0.1:${HONUA_GP_PEER_PORT}")
unexpected_total=0

compose() { docker compose --project-name "${project}" -f "${compose_file}" "$@"; }
staging() { grep -m1 -E "^[[:space:]]*Geoprocessing__OutputStaging__$1:" "${compose_file}" | cut -d: -f2- | tr -d '" '; }
cleanup() { compose down -v --remove-orphans >/dev/null 2>&1; }
trap cleanup EXIT

# One probe line: status, reason header, Age (a replayed answer carries one),
# Cache-Control, and the liveness status sampled at the same instant.
probe() {
  local url="$1" headers status reason age cache live
  headers="$(curl --silent --max-time 5 -D - -o /dev/null "${url}/healthz/ready" | tr -d '\r')"
  status="$(awk 'NR==1{print $2}' <<<"${headers}")"
  reason="$(grep -i '^X-Honua-Readiness-Reason:' <<<"${headers}" | cut -d' ' -f2-)"
  age="$(grep -i '^Age:' <<<"${headers}" | cut -d' ' -f2-)"
  cache="$(grep -i '^Cache-Control:' <<<"${headers}" | cut -d' ' -f2-)"
  live="$(curl --silent --max-time 5 -o /dev/null -w '%{http_code}' "${url}/healthz/live")"
  printf 'ready=%s reason=%s age=%s cache-control=%s live=%s\n' \
    "${status:-none}" "${reason:-none}" "${age:-none}" "${cache:-none}" "${live}"
}

warm() {
  local url
  for url in "${urls[@]}"; do
    echo "warm ${url} $(probe "${url}")"
    echo "warm ${url} $(probe "${url}")"
  done
}

# Six probes one second apart on each host; any 200 while the marker is bad is
# an unexpected ready answer, as is a non-200 liveness or a missing reason.
disrupted_probes() {
  local label="$1" i url line unexpected=0
  for i in 1 2 3 4 5 6; do
    for url in "${urls[@]}"; do
      line="$(probe "${url}")"
      echo "${label} probe ${i} ${url} ${line}"
      [[ "${line}" == ready=200* ]] && unexpected=$((unexpected + 1))
      [[ "${line}" == *"live=200" ]] || { echo "${label} FAIL liveness affected"; unexpected=$((unexpected + 1)); }
      [[ "${line}" == *"reason=gp-output-store-attestation-unavailable"* ]] || { echo "${label} FAIL reason missing"; unexpected=$((unexpected + 1)); }
    done
    sleep 1
  done
  echo "${label} unexpected=${unexpected} of 12 probes (6 per host)"
  unexpected_total=$((unexpected_total + unexpected))
}

recovered() {
  local label="$1" url deadline=$((SECONDS + 30)) line
  for url in "${urls[@]}"; do
    until line="$(probe "${url}")"; [[ "${line}" == ready=200* ]]; do
      (( SECONDS < deadline )) || { echo "${label} FAIL ${url} did not recover: ${line}"; unexpected_total=$((unexpected_total + 1)); return; }
      sleep 1
    done
    echo "${label} recovered ${url} after $((SECONDS - deadline + 30))s ${line}"
  done
}

echo "started $(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo "image ${HONUA_SERVER_IMAGE}"
echo "image revision $(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "${HONUA_SERVER_IMAGE}")"
mkdir -p "${HONUA_GP_OBJECT_ROOT}" && chmod 777 "${HONUA_GP_OBJECT_ROOT}"
computed="$("${repo_root}/scripts/operations/initialize-gp-output-store.sh" \
  --root-path "${HONUA_GP_OBJECT_ROOT}" --store-reference "$(staging StoreReference)" \
  --persistence-class "$(staging PersistenceClass)" --backup-identity "$(staging BackupIdentity)" \
  --backup-store-references "$(staging BackupStoreReferences__0)" --key-prefix "$(staging KeyPrefix)" \
  --max-inline-artifact-bytes "$(staging MaxInlineArtifactBytes)" --read-lease-duration "$(staging ReadLeaseDuration)" \
  --sweep-interval "$(staging SweepInterval)" --sweep-grace "$(staging SweepGrace)" \
  --orphan-retention "$(staging OrphanRetention)")" || { echo "FAIL provisioning"; exit 1; }
chmod 644 "${marker}"
[[ "${computed}" == "$(staging ConfigurationDigest)" ]] || { echo "FAIL digest ${computed}"; exit 1; }
echo "marker provisioned digest ${computed}"

compose up -d --wait postgres redis server server-peer >/dev/null 2>&1 || { echo "FAIL topology did not become healthy"; compose logs server | tail -40; exit 1; }
for service in server server-peer; do
  echo "container ${service} image $(docker inspect --format '{{.Image}}' "$(compose ps -q "${service}")")"
done
original="$(cat "${marker}")"

echo "== baseline (marker present)"
warm

echo "== marker removed (#4770 reproduction)"
mv "${marker}" "${marker}.removed"
disrupted_probes removed
mv "${marker}.removed" "${marker}"
recovered removed

echo "== marker corrupted (invalid JSON)"
warm
printf '{"SchemaVersion":1,"ConfigurationDigest":' > "${marker}"
disrupted_probes corrupt-json
printf '%s' "${original}" > "${marker}"
recovered corrupt-json

echo "== marker corrupted (well-formed, wrong configuration digest)"
warm
jq -c '.ConfigurationDigest = ("0" * 64)' <<<"${original}" > "${marker}"
disrupted_probes corrupt-digest
printf '%s' "${original}" > "${marker}"
recovered corrupt-digest

echo "authenticated probe after restore $(curl --silent -H 'X-API-Key: gp-reliability-admin' -o /dev/null -w '%{http_code}' "${urls[0]}/healthz/ready")"
echo "finished $(date -u +%Y-%m-%dT%H:%M:%SZ) unexpected_total=${unexpected_total}"
[[ "${unexpected_total}" == 0 ]]
