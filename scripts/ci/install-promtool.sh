#!/usr/bin/env bash
set -euo pipefail

destination="${1:?Usage: install-promtool.sh OUTPUT_DIRECTORY}"
version=3.5.0
archive="prometheus-${version}.linux-amd64.tar.gz"
archive_sha256=e811827af26d822afb09a4f28314f61b618b12cff5369835a67f674d8b46f39a

if [[ "$(uname -s)" != Linux || "$(uname -m)" != x86_64 ]]; then
  echo "promtool installer requires Linux x86_64" >&2
  exit 1
fi

download_directory=$(mktemp -d)
trap 'rm -rf "${download_directory}"' EXIT
curl --fail --silent --show-error --location --retry 3 \
  "https://github.com/prometheus/prometheus/releases/download/v${version}/${archive}" \
  --output "${download_directory}/${archive}"
printf '%s  %s\n' "${archive_sha256}" "${download_directory}/${archive}" | sha256sum --check --status
tar -xzf "${download_directory}/${archive}" -C "${download_directory}" \
  "prometheus-${version}.linux-amd64/promtool"
mkdir -p "${destination}"
install -m 0755 "${download_directory}/prometheus-${version}.linux-amd64/promtool" "${destination}/promtool"
"${destination}/promtool" --version
