#!/usr/bin/env bash
# Install-mode cases for the GDAL CLI provisioner. The self-test sources this file.

prepare_stub_bin() {
  local bin=$1
  local name
  cat > "${bin}/sudo" << 'EOF'
#!/bin/bash
while [[ "${1:-}" == *=* ]]; do
  shift
done
exec "$@"
EOF
  cat > "${bin}/timeout" << 'EOF'
#!/bin/bash
while [[ $# -gt 0 ]]; do
  case "$1" in
    --kill-after=*) shift ;;
    --kill-after) shift 2 ;;
    *)
      if [[ "$1" =~ ^[0-9]+s?$ ]]; then
        shift
        continue
      fi
      break
      ;;
  esac
done
exec "$@"
EOF
  cat > "${bin}/apt-get" << 'EOF'
#!/bin/bash
printf '%s\n' "$*" >> "${HONUA_APT_LOG:?}"
if [[ "${HONUA_APT_FAIL_NO_DOWNLOAD:-}" == "1" && "$*" == *--no-download* ]]; then
  exit 1
fi
if [[ "$*" == *--download-only* ]]; then
  cp "${HONUA_DEB_FIXTURE_DIR:?}"/*.deb "${HONUA_APT_ARCHIVE_DIR:?}/"
fi
exit 0
EOF
  cat > "${bin}/dpkg" << 'EOF'
#!/bin/bash
printf '%s\n' "$*" >> "${HONUA_APT_LOG:?}"
exit 0
EOF
  cat > "${bin}/python3" << 'EOF'
#!/bin/bash
exit 0
EOF
  cat > "${bin}/gdalinfo" << 'EOF'
#!/bin/bash
echo "GDAL 3.8.4, released 2024/02/08"
EOF
  for name in gdaldem ogr2ogr gdal_translate gdal_proximity.py sleep; do
    printf '#!/bin/bash\nexit 0\n' > "${bin}/${name}"
  done
  chmod +x "${bin}/"*
}

run_install_case() {
  local mode=$1
  local root bin archive cache summary path_file manifest fixture decoy status
  root="$(mktemp -d)"
  bin="${root}/bin"
  archive="${root}/archives"
  cache="${root}/cache"
  summary="${root}/summary"
  path_file="${root}/github-path"
  manifest="${root}/runner/gdal-cli-debs.manifest"
  fixture="${root}/debs"
  decoy="${root}/decoy"
  mkdir -p "$bin" "$archive" "${root}/apt-conf" "$fixture" "${root}/runner" "$decoy"
  prepare_stub_bin "$bin"
  printf '#!/bin/bash\necho decoy\n' > "${decoy}/gdaldem"
  chmod +x "${decoy}/gdaldem"
  printf 'deb\n' > "${fixture}/gdal-bin_3.8.4+dfsg-3ubuntu3_amd64.deb"
  printf 'gdal-bin_3.8.4+dfsg-3ubuntu3_amd64.deb\n' > "$manifest"
  : > "${root}/apt.log"
  if [[ "$mode" == "hit" || "$mode" == "missing-tool" || "$mode" == "fallback" || "$mode" == "hit-install-fails" ]]; then
    mkdir -p "$cache"
    cp "${fixture}/gdal-bin_3.8.4+dfsg-3ubuntu3_amd64.deb" "$cache/"
  fi
  if [[ "$mode" == "missing-tool" ]]; then
    rm -f "${bin}/gdaldem"
  fi
  if [[ "$mode" == "empty-hit" || "$mode" == "empty-fallback" ]]; then
    rm -rf "$cache"
    mkdir -p "$cache"
  fi
  if [[ "$mode" == "partial-miss" ]]; then
    printf 'missing-package_1_amd64.deb\n' >> "$manifest"
  fi
  if [[ "$mode" == "extra-archive" ]]; then
    printf 'deb\n' > "${fixture}/unrelated_1_amd64.deb"
  fi
  set +e
  (
    set -euo pipefail
    export PATH="${bin}:${decoy}:${PATH}"
    export HONUA_APT_ARCHIVE_DIR="$archive"
    export HONUA_APT_CONF_DIR="${root}/apt-conf"
    export HONUA_APT_LOG="${root}/apt.log"
    export HONUA_DEB_FIXTURE_DIR="$fixture"
    export HONUA_GDAL_PYTHON="${bin}/python3"
    export HONUA_GDAL_TOOL_PATH="$bin"
    export GITHUB_PATH="$path_file"
    export GITHUB_STEP_SUMMARY="$summary"
    export RUNNER_TEMP="${root}/runner"
    export HONUA_GDAL_ERROR_MARK="provision-error: "
    export GDAL_DEB_CACHE_DIR="$cache"
    unset GDAL_DEB_CACHE_HIT
    case "$mode" in
      hit|missing-tool|empty-hit)
        export GDAL_INDEX_STATE=ok
        export GDAL_DEB_CACHE_HIT=true
        ;;
      hit-install-fails)
        export GDAL_INDEX_STATE=ok
        export GDAL_DEB_CACHE_HIT=true
        export HONUA_APT_FAIL_NO_DOWNLOAD=1
        ;;
      miss|partial-miss|extra-archive)
        export GDAL_INDEX_STATE=ok
        export GDAL_DEB_CACHE_HIT=false
        ;;
      unset-miss)
        export GDAL_INDEX_STATE=ok
        ;;
      fallback|empty-fallback)
        export GDAL_INDEX_STATE=unavailable
        ;;
    esac
    cmd_install
  )
  status=$?
  set -e
  case "$mode" in
    hit)
      [[ "$status" -eq 0 ]] || { echo "cache hit install failed" >&2; exit 1; }
      grep -q -- '--no-download' "${root}/apt.log" || { echo "cache hit did not install offline" >&2; exit 1; }
      if grep -Eq 'update|--download-only' "${root}/apt.log"; then
        echo "cache hit reached the apt mirror: $(cat "${root}/apt.log")" >&2
        exit 1
      fi
      [[ -f "${archive}/gdal-bin_3.8.4+dfsg-3ubuntu3_amd64.deb" ]] || {
        echo "cache hit did not install the saved deb" >&2
        exit 1
      }
      grep -qx '/usr/bin' "$path_file" || { echo "cache hit did not publish /usr/bin" >&2; exit 1; }
      grep -q 'Cache: hit' "$summary" || { echo "cache hit did not record the summary" >&2; exit 1; }
      ;;
    fallback)
      [[ "$status" -eq 0 ]] || { echo "saved-image install failed" >&2; exit 1; }
      grep -q -- '-i ' "${root}/apt.log" || { echo "saved-image install did not run dpkg -i" >&2; exit 1; }
      grep -q 'gdal-bin_3.8.4+dfsg-3ubuntu3_amd64.deb' "${root}/apt.log" || {
        echo "saved-image install did not install the cached deb" >&2
        exit 1
      }
      if grep -Eq 'update|--download-only|--no-download' "${root}/apt.log"; then
        echo "saved-image install reached apt: $(cat "${root}/apt.log")" >&2
        exit 1
      fi
      grep -q 'Cache: saved-image' "$summary" || { echo "saved-image install did not record the summary" >&2; exit 1; }
      ;;
    miss|unset-miss|extra-archive)
      [[ "$status" -eq 0 ]] || { echo "${mode} install failed" >&2; exit 1; }
      grep -q -- '--error-on=any' "${root}/apt.log" || { echo "${mode} ignored a failed apt fetch" >&2; exit 1; }
      grep -q -- '--download-only' "${root}/apt.log" || { echo "${mode} did not download" >&2; exit 1; }
      grep -q -- '--no-download' "${root}/apt.log" || { echo "${mode} did not install offline" >&2; exit 1; }
      [[ -f "${cache}/gdal-bin_3.8.4+dfsg-3ubuntu3_amd64.deb" ]] || {
        echo "${mode} did not save the downloaded deb" >&2
        exit 1
      }
      if [[ "$mode" == "extra-archive" && -f "${cache}/unrelated_1_amd64.deb" ]]; then
        echo "cache miss saved a deb that was not in the manifest" >&2
        exit 1
      fi
      grep -q 'Cache: miss' "$summary" || { echo "${mode} did not record the summary" >&2; exit 1; }
      ;;
    partial-miss)
      [[ "$status" -eq 0 ]] || { echo "partial manifest install failed" >&2; exit 1; }
      [[ ! -d "$cache" ]] || { echo "partial manifest saved a cache: $(ls "$cache")" >&2; exit 1; }
      ;;
    hit-install-fails|empty-hit|empty-fallback|missing-tool)
      [[ "$status" -ne 0 ]] || { echo "${mode} was accepted" >&2; exit 1; }
      if [[ "$mode" == "hit-install-fails" ]] && grep -Eq 'update|--download-only' "${root}/apt.log"; then
        echo "a failed cached install reached the apt mirror: $(cat "${root}/apt.log")" >&2
        exit 1
      fi
      ;;
    *)
      echo "unknown install case ${mode}" >&2
      exit 1
      ;;
  esac
  rm -rf "$root"
}
