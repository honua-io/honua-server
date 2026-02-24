#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd)"

SPEC_PATH="${REPO_ROOT}/docs/api-specs/admin-api.json"
OUTPUT_DIR="${1:-${REPO_ROOT}/artifacts/control-plane-sdks}"
GENERATOR_IMAGE="${OPENAPI_GENERATOR_IMAGE:-openapitools/openapi-generator-cli:v7.12.0}"

if [[ ! -f "${SPEC_PATH}" ]]; then
  echo "Admin API spec not found: ${SPEC_PATH}" >&2
  exit 1
fi

RAW_VERSION="${SDK_VERSION:-}"
if [[ -z "${RAW_VERSION}" ]]; then
  if git -C "${REPO_ROOT}" describe --tags --exact-match >/dev/null 2>&1; then
    RAW_VERSION="$(git -C "${REPO_ROOT}" describe --tags --exact-match)"
  else
    RAW_VERSION="0.0.0-dev.$(git -C "${REPO_ROOT}" rev-parse --short HEAD)"
  fi
fi

PACKAGE_VERSION="${RAW_VERSION#v}"
ARTIFACT_VERSION="$(printf '%s' "${PACKAGE_VERSION}" | sed -E 's/[^0-9A-Za-z._-]+/-/g')"
if [[ -z "${ARTIFACT_VERSION}" ]]; then
  ARTIFACT_VERSION="0.0.0-dev"
fi

if [[ "${PACKAGE_VERSION}" =~ ^([0-9]+\.[0-9]+\.[0-9]+) ]]; then
  PYTHON_PACKAGE_VERSION="${BASH_REMATCH[1]}"
else
  PYTHON_PACKAGE_VERSION="0.0.0"
fi

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "${TMP_DIR}"' EXIT

mkdir -p "${OUTPUT_DIR}"

run_generator() {
  local generator="$1"
  local output_subdir="$2"
  local additional_properties="$3"

  docker run --rm \
    --user "$(id -u):$(id -g)" \
    -v "${REPO_ROOT}/docs/api-specs:/spec:ro" \
    -v "${TMP_DIR}:/out" \
    "${GENERATOR_IMAGE}" generate \
      -i /spec/admin-api.json \
      -g "${generator}" \
      -o "/out/${output_subdir}" \
      --global-property apiDocs=false,modelDocs=false \
      --additional-properties "${additional_properties}"
}

echo "Generating control-plane SDKs from ${SPEC_PATH}"
echo "Generator image: ${GENERATOR_IMAGE}"
echo "Version: ${PACKAGE_VERSION}"

run_generator \
  "typescript-fetch" \
  "typescript" \
  "npmName=@honua/control-plane-sdk,npmVersion=${PACKAGE_VERSION},typescriptThreePlus=true,modelPropertyNaming=original"

run_generator \
  "python" \
  "python" \
  "packageName=honua_control_plane_sdk,projectName=honua-control-plane-sdk,packageVersion=${PYTHON_PACKAGE_VERSION}"

run_generator \
  "csharp" \
  "dotnet" \
  "packageName=Honua.ControlPlane.Sdk,packageVersion=${PACKAGE_VERSION},targetFramework=net8.0"

TS_ARCHIVE="${OUTPUT_DIR}/honua-control-plane-sdk-typescript-${ARTIFACT_VERSION}.tar.gz"
PY_ARCHIVE="${OUTPUT_DIR}/honua-control-plane-sdk-python-${ARTIFACT_VERSION}.tar.gz"
CS_ARCHIVE="${OUTPUT_DIR}/honua-control-plane-sdk-dotnet-${ARTIFACT_VERSION}.tar.gz"

tar -C "${TMP_DIR}" -czf "${TS_ARCHIVE}" typescript
tar -C "${TMP_DIR}" -czf "${PY_ARCHIVE}" python
tar -C "${TMP_DIR}" -czf "${CS_ARCHIVE}" dotnet

cat > "${OUTPUT_DIR}/manifest.json" <<EOF
{
  "spec": "docs/api-specs/admin-api.json",
  "generatorImage": "${GENERATOR_IMAGE}",
  "version": "${PACKAGE_VERSION}",
  "generatedAtUtc": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "commit": "$(git -C "${REPO_ROOT}" rev-parse HEAD)",
  "artifacts": [
    "$(basename "${TS_ARCHIVE}")",
    "$(basename "${PY_ARCHIVE}")",
    "$(basename "${CS_ARCHIVE}")"
  ]
}
EOF

(
  cd "${OUTPUT_DIR}"
  sha256sum \
    "$(basename "${TS_ARCHIVE}")" \
    "$(basename "${PY_ARCHIVE}")" \
    "$(basename "${CS_ARCHIVE}")" \
    > SHA256SUMS.txt
)

echo "Generated SDK artifacts:"
echo "- ${TS_ARCHIVE}"
echo "- ${PY_ARCHIVE}"
echo "- ${CS_ARCHIVE}"
echo "- ${OUTPUT_DIR}/manifest.json"
echo "- ${OUTPUT_DIR}/SHA256SUMS.txt"
