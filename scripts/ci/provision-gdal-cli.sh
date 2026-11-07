#!/usr/bin/env bash
set -euo pipefail

readonly APT_ATTEMPT_SECONDS=90
readonly APT_INSTALL_SECONDS=180
readonly APT_KILL_GRACE_SECONDS=15
readonly APT_BACKOFF_SECONDS=5
package_names=(gdal-bin python3-gdal python3-numpy python3-scipy libatomic1)

cache_directory() {
  printf '%s/gdal-cli-debs' "${RUNNER_TEMP:?runner temp is unset}"
}

cmd_resolve() {
  printf 'image=%s-%s\ncache-dir=%s\n' "${ImageOS:?runner image is unset}" \
    "${ImageVersion:?runner image version is unset}" "$(cache_directory)" >> "${GITHUB_OUTPUT:?}"
}

download_from_mirror() {
  local attempt
  for attempt in 1 2 3; do
    if sudo timeout --kill-after="${APT_KILL_GRACE_SECONDS}s" "${APT_ATTEMPT_SECONDS}s" \
        env DEBIAN_FRONTEND=noninteractive bash -c '
          package_count=$1
          shift
          packages=("${@:1:package_count}")
          shift "$package_count"
          apt-get "$@" update --error-on=any -q &&
          apt-get "$@" install -yq --reinstall --download-only "${packages[@]}"
        ' bash "${#package_names[@]}" "${package_names[@]}" "${apt_options[@]}"; then
      return 0
    fi
    echo "::warning::GDAL package download attempt ${attempt} failed or stalled"
    if [[ "$attempt" -lt 3 ]]; then
      sleep $((attempt * APT_BACKOFF_SECONDS))
    fi
  done
  return 1
}

download_packages() {
  local download_dir=$1
  local fallback_sources
  apt_options=(-o Acquire::Retries=1 -o Acquire::http::Timeout=30 -o Acquire::https::Timeout=30
    -o "Dir::Cache::archives=${download_dir}")
  if download_from_mirror; then
    return 0
  fi
  echo "::warning::Retrying GDAL packages against the HTTPS Ubuntu archive mirror"
  fallback_sources="${RUNNER_TEMP}/gdal-cli-sources"
  mkdir -p "$fallback_sources"
  chmod 755 "$fallback_sources"
  sed 's|^URIs:.*|URIs: https://archive.ubuntu.com/ubuntu|' \
    "${HONUA_APT_SOURCES_FILE:-/etc/apt/sources.list.d/ubuntu.sources}" \
    > "${fallback_sources}/ubuntu.sources"
  apt_options+=(-o Dir::Etc::sourcelist=/dev/null -o "Dir::Etc::sourceparts=${fallback_sources}")
  if ! download_from_mirror; then
    echo '::error::GDAL package downloads failed on both mirrors'
    return 1
  fi
}

prepare_cache() {
  local cache_dir=$1
  local download_dir
  download_dir=$(mktemp -d "${RUNNER_TEMP}/gdal-cli-download.XXXXXX")
  chmod 755 "$download_dir"
  mkdir -p "${download_dir}/partial"
  download_packages "$download_dir"
  mkdir -p "$cache_dir"
  cp "${download_dir}"/*.deb "${cache_dir}/"
  (cd "$cache_dir" && sha256sum -- *.deb > SHA256SUMS)
}

validate_cache() {
  local cache_dir=$1
  (cd "$cache_dir" && sha256sum --check --strict SHA256SUMS)
  (cd "$cache_dir" && sha256sum -- *.deb) | cmp - "${cache_dir}/SHA256SUMS"
}

install_offline() {
  local cache_dir=$1
  local install_dir
  install_dir=$(mktemp -d "${RUNNER_TEMP}/gdal-cli-install.XXXXXX")
  chmod 755 "$install_dir"
  mkdir -p "${install_dir}/partial"
  cp "${cache_dir}"/*.deb "${install_dir}/"
  sudo timeout --kill-after="${APT_KILL_GRACE_SECONDS}s" "${APT_INSTALL_SECONDS}s" \
    env DEBIAN_FRONTEND=noninteractive apt-get -o "Dir::Cache::archives=${install_dir}" \
    install -yq --no-download "${install_dir}"/*.deb
}

verify_gdal_cli() {
  local package status tool
  for package in "${package_names[@]}"; do
    status=$(dpkg-query -W -f='${db:Status-Status}' "$package")
    if [[ "$status" != installed ]]; then
      echo "::error::Native dependency ${package} is not installed"
      return 1
    fi
  done
  (
    export PATH="${HONUA_GDAL_TOOL_PATH:-/usr/bin:/bin}"
    "${HONUA_GDAL_PYTHON:-/usr/bin/python3}" -c 'from osgeo import gdal; from scipy import ndimage; import numpy'
    for tool in gdaldem ogr2ogr ogrinfo gdal_translate gdalinfo gdal_proximity.py python3; do
      command -v "$tool" >/dev/null || { echo "::error::${tool} is missing after GDAL installation"; exit 1; }
    done
    gdalinfo --version
    ogrinfo --version
  )
}

cmd_install() {
  local cache_dir
  cache_dir=$(cache_directory)
  if [[ "${GDAL_DEB_CACHE_HIT:-false}" == true ]]; then
    echo 'GDAL package cache: hit; installing offline'
  else
    echo 'GDAL package cache: miss; downloading with bounded mirror fallback'
    prepare_cache "$cache_dir"
  fi
  validate_cache "$cache_dir"
  install_offline "$cache_dir"
  verify_gdal_cli
  case "${GDAL_PYTHON_ENVIRONMENT:-system}" in
    system) echo /usr/bin >> "${GITHUB_PATH:?}" ;;
    setup-python) ;;
    *) echo '::error::Unknown GDAL Python environment'; return 1 ;;
  esac
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  case "${1:-}" in
    resolve) cmd_resolve ;;
    install) cmd_install ;;
    self-test) exec python3 "$(dirname "$0")/fixtures/validate-gdal-cli-cache.py" ;;
    *) echo 'usage: provision-gdal-cli.sh resolve|install|self-test' >&2; exit 2 ;;
  esac
fi
