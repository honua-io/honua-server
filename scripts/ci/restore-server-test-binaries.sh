#!/usr/bin/env bash
# Fail-closed restoration of an exact-head server-test binary artifact.

set -euo pipefail

CONTRACT="honua.server-test-binaries.v1"
MAX_ARCHIVE_BYTES="${HONUA_SERVER_TEST_ARTIFACT_MAX_ARCHIVE_BYTES:-268435456}"
MAX_UNPACKED_BYTES="${HONUA_SERVER_TEST_ARTIFACT_MAX_UNPACKED_BYTES:-536870912}"
manifest=""
destination=""
expected_project=""
expected_source_sha=""

usage() {
  echo "Usage: $0 --manifest <file> --destination <repo-root> --project <relative.csproj> --source-sha <commit>" >&2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --manifest) manifest="${2:-}"; shift 2 ;;
    --destination) destination="${2:-}"; shift 2 ;;
    --project) expected_project="${2:-}"; shift 2 ;;
    --source-sha) expected_source_sha="${2:-}"; shift 2 ;;
    *) usage; exit 2 ;;
  esac
done

if [[ -z "${manifest}" || -z "${destination}" || -z "${expected_project}" || -z "${expected_source_sha}" ]]; then
  usage
  exit 2
fi
for command in jq sha256sum tar; do
  command -v "${command}" >/dev/null || { echo "::error::Required command '${command}' is unavailable." >&2; exit 2; }
done
[[ -f "${manifest}" ]] || { echo "::error::Artifact manifest is missing." >&2; exit 1; }
mkdir -p "${destination}"
destination="$(cd "${destination}" && pwd)"
manifest_dir="$(cd "$(dirname "${manifest}")" && pwd)"

dotnet_sdk="${HONUA_SERVER_TEST_ARTIFACT_DOTNET_SDK:-}"
if [[ -z "${dotnet_sdk}" ]]; then
  command -v dotnet >/dev/null || { echo "::error::Required command 'dotnet' is unavailable." >&2; exit 2; }
  dotnet_sdk="$(dotnet --version)"
fi

jq -e \
  --arg contract "${CONTRACT}" \
  --arg project "${expected_project}" \
  --arg source_sha "${expected_source_sha,,}" \
  --arg dotnet_sdk "${dotnet_sdk}" '
    .contract == $contract and
    .project == $project and
    .source_sha == $source_sha and
    .dotnet_sdk == $dotnet_sdk and
    (.archive_file | type == "string" and test("^server-test-binaries-[a-z0-9-]+\\.tar\\.gz$")) and
    (.archive_sha256 | type == "string" and test("^[0-9a-f]{64}$")) and
    (.archive_bytes | type == "number" and . > 0) and
    (.unpacked_bytes | type == "number" and . > 0) and
    (.file_count | type == "number" and . > 0)
  ' "${manifest}" >/dev/null || {
  echo "::error::Artifact manifest does not match the exact source/project/toolchain contract." >&2
  exit 1
}

archive_file="$(jq -r '.archive_file' "${manifest}")"
archive_path="${manifest_dir}/${archive_file}"
[[ -f "${archive_path}" ]] || { echo "::error::Artifact archive is missing." >&2; exit 1; }
actual_bytes="$(stat -c %s "${archive_path}")"
manifest_bytes="$(jq -r '.archive_bytes' "${manifest}")"
unpacked_bytes="$(jq -r '.unpacked_bytes' "${manifest}")"
if (( actual_bytes != manifest_bytes || actual_bytes > MAX_ARCHIVE_BYTES || unpacked_bytes > MAX_UNPACKED_BYTES )); then
  echo "::error::Artifact size does not match the bounded manifest." >&2
  exit 1
fi
expected_digest="$(jq -r '.archive_sha256' "${manifest}")"
actual_digest="$(sha256sum "${archive_path}" | cut -d' ' -f1)"
[[ "${actual_digest}" == "${expected_digest}" ]] || { echo "::error::Artifact SHA-256 integrity check failed." >&2; exit 1; }

# Refuse absolute or parent-traversal paths before extraction.
while IFS= read -r entry; do
  normalized="${entry#./}"
  if [[ -z "${normalized}" ]]; then
    continue
  fi
  if [[ "${normalized}" == /* || "/${normalized}/" == *"/../"* ]]; then
    echo "::error::Artifact contains an unsafe archive path: ${entry}" >&2
    exit 1
  fi
done < <(tar -tzf "${archive_path}")

tar -xzf "${archive_path}" -C "${destination}"
project_dir="${destination}/$(dirname "${expected_project}")"
configuration="${HONUA_SERVER_TEST_ARTIFACT_CONFIGURATION:-Release}"
[[ -f "${project_dir}/obj/project.assets.json" ]] || { echo "::error::Restored project assets are missing." >&2; exit 1; }
find "${project_dir}/bin/${configuration}" -type f -name '*.dll' -print -quit | grep -q . || {
  echo "::error::Restored test binaries are missing." >&2
  exit 1
}
find "${project_dir}/bin/${configuration}" -type f -name '*.pdb' -print -quit | grep -q . || {
  echo "::error::Restored PDB failure-attribution evidence is missing." >&2
  exit 1
}

echo "Restored ${expected_project} from ${archive_file} (${actual_bytes} bytes)."
