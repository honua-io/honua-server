#!/usr/bin/env bash
set -euo pipefail

printf '%s\n' \
  'Acquire::Retries "3";' \
  'Acquire::http::Timeout "30";' \
  'Acquire::https::Timeout "30";' \
  | sudo tee "${HONUA_APT_CONF_DIR:-/etc/apt/apt.conf.d}/80honua-ci-network" >/dev/null

apt_options=()
fallback_sources=""
trap 'if [[ -n "$fallback_sources" ]]; then rm -rf "$fallback_sources"; fi' EXIT

download_packages() {
  local attempt
  for attempt in 1 2 3; do
    if sudo timeout --kill-after=15s 240s sh -c '
        apt-get "$@" update --error-on=any -q &&
        apt-get "$@" install -yq --download-only gdal-bin python3-gdal python3-numpy python3-scipy
        ' sh "${apt_options[@]}"; then
      return 0
    fi
    echo "::warning::GDAL CLI package download attempt ${attempt} failed or stalled on the apt mirror"
    if [[ "${attempt}" -lt 3 ]]; then
      sleep $((attempt * 15))
    fi
  done
  return 1
}

if ! download_packages; then
  echo "::warning::Retrying GDAL CLI packages against https://archive.ubuntu.com/ubuntu"
  fallback_sources=$(mktemp -d)
  chmod 755 "$fallback_sources"
  sed 's|^URIs:.*|URIs: https://archive.ubuntu.com/ubuntu|' \
    "${HONUA_APT_SOURCES_FILE:-/etc/apt/sources.list.d/ubuntu.sources}" \
    > "$fallback_sources/ubuntu.sources"
  apt_options=(-o Dir::Etc::sourcelist=/dev/null -o "Dir::Etc::sourceparts=$fallback_sources")
  if ! download_packages; then
    echo "::error::the GDAL CLI packages could not be downloaded after 3 bounded attempts on each Ubuntu mirror"
    exit 1
  fi
fi

sudo timeout --kill-after=15s 600s apt-get "${apt_options[@]}" install -yqq --no-download \
  gdal-bin python3-gdal python3-numpy python3-scipy
gdalinfo --version
