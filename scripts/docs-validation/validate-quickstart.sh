#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
document="${repo_root}/docs/get-started/quickstart.md"
extractor="${repo_root}/scripts/docs-validation/extract-fenced-commands.py"
project_name="${HONUA_DOCS_COMPOSE_PROJECT:-honua-docs-quickstart}"
artifacts="${HONUA_DOCS_ARTIFACTS:-${repo_root}/artifacts/docs-validation/quickstart}"
http_port="${HONUA_DOCS_HTTP_PORT:-18080}"
grpc_port="${HONUA_DOCS_GRPC_PORT:-18081}"
postgres_port="${HONUA_DOCS_POSTGRES_PORT:-15432}"
redis_port="${HONUA_DOCS_REDIS_PORT:-16379}"
preserve_stack="${HONUA_DOCS_PRESERVE_STACK:-0}"
base_url="http://localhost:${http_port}"
run_dir=$(mktemp -d "${repo_root}/.docs-validation-quickstart.XXXXXX")
extracted="${run_dir}/quickstart.sh"
export HONUA_HTTP_PORT="${http_port}"
export HONUA_GRPC_PORT="${grpc_port}"
export POSTGRES_PORT="${postgres_port}"
export REDIS_PORT="${redis_port}"
export HONUA_STORAGE_VOLUME_NAME="${project_name}_storage"
compose=(docker compose --project-name "${project_name}" --project-directory "${repo_root}")

cleanup() {
  local exit_code=$?
  cp "${run_dir}"/*.json "${run_dir}"/map.html "${artifacts}/" 2>/dev/null || true
  "${compose[@]}" logs --no-color > "${artifacts}/compose.log" 2>&1 || true
  "${compose[@]}" ps --all > "${artifacts}/compose-ps.txt" 2>&1 || true
  if [ "${preserve_stack}" = "1" ]; then
    echo "preserving Compose project ${project_name} for subsequent journey stages"
  else
    "${compose[@]}" down --volumes --remove-orphans || true
  fi
  rm -rf "${run_dir}"
  exit "${exit_code}"
}
trap cleanup EXIT

for command in docker curl gh jq python3; do
  command -v "${command}" >/dev/null || { echo "missing prerequisite: ${command}" >&2; exit 1; }
done
docker compose version >/dev/null
mkdir -p "${artifacts}"

python3 "${extractor}" "${document}" --list --output "${extracted}" | tee "${artifacts}/blocks.txt"

# A validation run owns an isolated Compose project and always begins without its
# containers or volumes, proving the documented bootstrap against a clean install.
"${compose[@]}" down --volumes --remove-orphans

(
  cd "${run_dir}"
  export COMPOSE_PROJECT_NAME="${project_name}"
  export COMPOSE_FILE="${repo_root}/docker-compose.yml"
  export HONUA_REPO_ROOT="${repo_root}"
  export HONUA_BASE_URL="${base_url}"
  bash "${extracted}"
)

layer_id=$(cat "${run_dir}/.quickstart-layer-id")
test "$(curl --silent --fail "${base_url}/healthz/ready")" = "Ready"
"${compose[@]}" ps --format json | jq -s -e '
  length == 3 and all(.[]; .State == "running" and .Health == "healthy")'
curl --fail --silent --show-error \
  -H 'X-API-Key: quickstart-admin-password' \
  "${base_url}/rest/services/quickstart/FeatureServer/${layer_id}/query?f=json&where=1%3D1&outFields=*&returnGeometry=true" \
  | jq -e '.features | length == 3'
curl --fail --silent --show-error \
  -H 'X-API-Key: quickstart-admin-password' \
  "${base_url}/tiles/${layer_id}/tile.json" \
  | jq -e '.tiles | length > 0'
curl --fail --silent --show-error \
  -H 'X-API-Key: quickstart-admin-password' \
  "${base_url}/tiles/${layer_id}/12/655/1582.mvt" \
  --output "${artifacts}/sample.mvt"
test -s "${artifacts}/sample.mvt"
grep -Fq "const layerId = ${layer_id};" "${run_dir}/map.html"
grep -Fq "${base_url}/tiles/" "${run_dir}/map.html"

echo "quickstart validation passed: clean stack healthy, layer ${layer_id} published, 3 features and map endpoints verified"
