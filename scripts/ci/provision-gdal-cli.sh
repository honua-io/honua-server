#!/usr/bin/env bash
# Install the GDAL CLI packages the real-GDAL cases shell out to.
# A cache hit copies the saved .deb set into the apt archive and installs it
# with --no-download. A cache miss downloads from the apt mirror, then saves
# that .deb set. The caller keys the cache on the runner image and the
# package versions this script prints. When that index cannot be read, install
# the newest set already saved for this runner image and do not download.

set -euo pipefail

package_names=(gdal-bin python3-gdal python3-numpy python3-scipy)
packages="${package_names[*]}"

apt_archive_dir() {
  printf '%s' "${HONUA_APT_ARCHIVE_DIR:-/var/cache/apt/archives}"
}

apt_conf_dir() {
  printf '%s' "${HONUA_APT_CONF_DIR:-/etc/apt/apt.conf.d}"
}

report_error() {
  printf '%s%s\n' "${HONUA_GDAL_ERROR_MARK:-::error::}" "$*"
}

configure_apt_timeouts() {
  printf '%s\n' \
    'Acquire::Retries "3";' \
    'Acquire::http::Timeout "30";' \
    'Acquire::https::Timeout "30";' \
    | sudo tee "$(apt_conf_dir)/80honua-ci-network" >/dev/null
}

sanitize_key_part() {
  printf '%s' "$1" | tr -c 'A-Za-z0-9._-' '-'
}

manifest_path() {
  printf '%s/gdal-cli-debs.manifest\n' "${RUNNER_TEMP:?runner temp is unset}"
}

candidate_version() {
  local package=$1
  if [[ -n "${HONUA_GDAL_CANDIDATE_FILE:-}" ]]; then
    awk -v package="$package" '$1 == package { print $2; exit }' "$HONUA_GDAL_CANDIDATE_FILE"
    return
  fi
  apt-cache policy "$package" | awk '/^[[:space:]]*Candidate:/ { print $2; exit }'
}

build_version_key() {
  local uris_file=$1
  local manifest_file=${2:-}
  local -a filenames=()
  local line package version key="" hash

  while IFS= read -r line || [[ -n "$line" ]]; do
    if [[ "$line" =~ ^\'[^[:space:]\']+\'[[:space:]]+([^[:space:]]+\.deb)([[:space:]]|$) ]]; then
      filenames+=("${BASH_REMATCH[1]}")
    fi
  done < "$uris_file"

  if [[ -s "$uris_file" && ${#filenames[@]} -eq 0 ]]; then
    report_error "could not read the GDAL CLI package filenames from apt" >&2
    return 1
  fi

  for package in "${package_names[@]}"; do
    version="$(candidate_version "$package")"
    if [[ -z "$version" || "$version" == "(none)" ]]; then
      report_error "no candidate version for ${package}" >&2
      return 1
    fi
    key+="${package}_$(sanitize_key_part "$version")__"
  done

  if [[ ${#filenames[@]} -eq 0 ]]; then
    hash="$(printf '' | sha256sum | awk '{print $1}')"
  else
    hash="$(printf '%s\n' "${filenames[@]}" | LC_ALL=C sort -u | sha256sum | awk '{print $1}')"
  fi
  key+="debs_${hash}"
  if [[ ${#key} -gt 400 ]]; then
    report_error "GDAL CLI package version key is ${#key} characters" >&2
    return 1
  fi
  if [[ ! "$key" =~ ^[A-Za-z0-9._-]+$ ]]; then
    report_error "GDAL CLI package version key contains characters a cache key rejects" >&2
    return 1
  fi

  if [[ -n "$manifest_file" ]]; then
    if [[ ${#filenames[@]} -eq 0 ]]; then
      : > "$manifest_file"
    else
      printf '%s\n' "${filenames[@]}" | LC_ALL=C sort -u > "$manifest_file"
    fi
  fi
  printf '%s\n' "$key"
}

refresh_package_index() {
  local attempt updated=false
  for attempt in 1 2 3; do
    if sudo timeout --kill-after=15s 60s apt-get update -q; then
      updated=true
      break
    fi
    echo "::warning::GDAL CLI package index update attempt ${attempt} failed or stalled on the apt mirror"
    if [[ "${attempt}" -lt 3 ]]; then
      sleep $((attempt * 15))
    fi
  done
  if [[ "${updated}" != "true" ]]; then
    return 1
  fi
}

download_packages() {
  local attempt downloaded=false
  for attempt in 1 2 3; do
    # shellcheck disable=SC2086 # word-splitting the package list is intended
    if sudo timeout --kill-after=15s 240s sh -c \
        "apt-get update -q && apt-get install -yq --download-only ${packages}"; then
      downloaded=true
      break
    fi
    echo "::warning::GDAL CLI package download attempt ${attempt} failed or stalled on the apt mirror"
    if [[ "${attempt}" -lt 3 ]]; then
      sleep $((attempt * 15))
    fi
  done
  if [[ "${downloaded}" != "true" ]]; then
    report_error "the GDAL CLI packages could not be downloaded from the runner's apt mirror after 3 bounded attempts"
    exit 1
  fi
}

install_offline() {
  # shellcheck disable=SC2086 # word-splitting the package list is intended
  sudo timeout --kill-after=15s 600s apt-get install -yqq --no-download ${packages}
}

copy_cached_debs() {
  local cache_dir=$1
  local -a debs
  shopt -s nullglob
  debs=("${cache_dir}"/*.deb)
  shopt -u nullglob
  if [[ ${#debs[@]} -eq 0 ]]; then
    report_error "the cached GDAL CLI package set is empty"
    exit 1
  fi
  sudo mkdir -p "$(apt_archive_dir)/partial"
  sudo cp "${debs[@]}" "$(apt_archive_dir)/"
}

stage_resolved_debs() {
  local cache_dir=$1
  local manifest_file=$2
  local name missing=0 staged
  if [[ ! -s "$manifest_file" ]]; then
    echo "GDAL CLI package download produced no .deb files to cache"
    return 0
  fi
  while IFS= read -r name; do
    [[ -z "$name" ]] && continue
    if [[ ! -f "$(apt_archive_dir)/${name}" ]]; then
      missing=1
      echo "::warning::resolved package ${name} is not in the apt archive; not saving the cache"
    fi
  done < "$manifest_file"
  if [[ "$missing" -ne 0 ]]; then
    return 0
  fi
  staged="$(mktemp -d)"
  while IFS= read -r name; do
    [[ -z "$name" ]] && continue
    cp "$(apt_archive_dir)/${name}" "${staged}/${name}"
  done < "$manifest_file"
  rm -rf "$cache_dir"
  mkdir -p "$(dirname "$cache_dir")"
  mv "$staged" "$cache_dir"
}

verify_gdal_cli() {
  local python_bin="${HONUA_GDAL_PYTHON:-/usr/bin/python3}"
  local tool
  if [[ -n "${GITHUB_PATH:-}" ]]; then
    echo "/usr/bin" >> "${GITHUB_PATH}"
  fi
  export PATH="/usr/bin:${PATH}"
  if [[ -n "${HONUA_GDAL_TOOL_PATH:-}" ]]; then
    export PATH="${HONUA_GDAL_TOOL_PATH}:${PATH}"
  fi
  "${python_bin}" -c 'from osgeo import gdal; from scipy import ndimage; import numpy'
  for tool in gdaldem ogr2ogr gdal_translate gdalinfo gdal_proximity.py python3; do
    command -v "${tool}" >/dev/null \
      || { report_error "${tool} is missing after installing the GDAL CLI packages"; exit 1; }
  done
  gdalinfo --version
}

note_summary() {
  local line=$1
  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    printf '%s\n' "$line" >> "${GITHUB_STEP_SUMMARY}"
  fi
}

write_resolve_outputs() {
  local index=$1
  local image=$2
  local versions=$3
  {
    echo "index=${index}"
    echo "image=${image}"
    echo "versions=${versions}"
    echo "cache-dir=.cache/gdal-cli-debs"
  } >> "${GITHUB_OUTPUT:?github output is unset}"
}

note_index_unavailable() {
  echo "::warning::GDAL CLI package index is unavailable; installing the newest saved .deb set for this runner image"
  note_summary "### GDAL CLI package resolution"
  note_summary ""
  note_summary "- Package index: unavailable"
}

cmd_resolve() {
  local uris_file image key manifest package version
  configure_apt_timeouts
  image="$(sanitize_key_part "${ImageOS:-unknown}-${ImageVersion:-unknown}")"
  echo "GDAL CLI runner image ${image}"
  if ! refresh_package_index; then
    note_index_unavailable
    write_resolve_outputs unavailable "$image" unavailable
    return 0
  fi
  uris_file="$(mktemp)"
  # shellcheck disable=SC2086 # word-splitting the package list is intended
  if ! sudo timeout --kill-after=15s 60s apt-get install --print-uris -yq ${packages} > "$uris_file"; then
    note_index_unavailable
    write_resolve_outputs unavailable "$image" unavailable
    return 0
  fi
  manifest="$(manifest_path)"
  key="$(build_version_key "$uris_file" "$manifest")"
  write_resolve_outputs ok "$image" "$key"
  for package in "${package_names[@]}"; do
    version="$(candidate_version "$package")"
    echo "GDAL CLI resolved ${package} ${version}"
  done
  echo "GDAL CLI version key ${key}"
  note_summary "### GDAL CLI package resolution"
  note_summary ""
  note_summary "- Runner image: \`${image}\`"
  note_summary "- Version key: \`${key}\`"
}

cmd_install() {
  local cache_dir="${GDAL_DEB_CACHE_DIR:?package cache directory is unset}"
  configure_apt_timeouts
  # An unreadable index cannot build the version key. Install the newest set
  # saved for this runner image, and do not touch the mirror again.
  if [[ "${GDAL_INDEX_STATE:-}" == "unavailable" ]]; then
    echo "GDAL CLI package index is unavailable; installing the newest saved .deb set with --no-download"
    note_summary "### GDAL CLI packages"
    note_summary ""
    note_summary "- Cache: saved-image"
    copy_cached_debs "$cache_dir"
    install_offline
  # actions/cache leaves cache-hit unset on a miss. Only an exact true is a hit.
  elif [[ "${GDAL_DEB_CACHE_HIT:-}" == "true" ]]; then
    echo "GDAL CLI package cache hit; installing the saved .deb set with --no-download"
    note_summary "### GDAL CLI packages"
    note_summary ""
    note_summary "- Cache: hit"
    copy_cached_debs "$cache_dir"
    install_offline
  else
    echo "GDAL CLI package cache miss; downloading the resolved packages from the apt mirror"
    note_summary "### GDAL CLI packages"
    note_summary ""
    note_summary "- Cache: miss"
    download_packages
    install_offline
    stage_resolved_debs "$cache_dir" "$(manifest_path)"
  fi
  verify_gdal_cli
}

usage() {
  echo "usage: provision-gdal-cli.sh resolve|install|self-test" >&2
  exit 2
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  case "${1:-}" in
    resolve) cmd_resolve ;;
    install) cmd_install ;;
    self-test)
      exec bash "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/fixtures/provision-gdal-cli-self-test.sh"
      ;;
    *) usage ;;
  esac
fi
