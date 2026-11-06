#!/usr/bin/env bash
# Exercise the GDAL CLI provisioner without apt, sudo, or the network.

set -euo pipefail

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/../provision-gdal-cli.sh"

write_fixture_uris() {
  local destination=$1
  cat > "$destination" << 'EOF'
'http://example.invalid/pool/gdal-bin_3.8.4+dfsg-3ubuntu3_amd64.deb' gdal-bin_3.8.4+dfsg-3ubuntu3_amd64.deb 10 SHA256:aa
'http://example.invalid/pool/python3-gdal_3.8.4+dfsg-3ubuntu3_amd64.deb' python3-gdal_3.8.4+dfsg-3ubuntu3_amd64.deb 11 SHA256:bb
EOF
}

assert_version_key() {
  local root uris manifest key changed_uris changed_key
  root="$(mktemp -d)"
  uris="${root}/uris"
  manifest="${root}/manifest"
  write_fixture_uris "$uris"
  HONUA_GDAL_CANDIDATE_FILE="${root}/candidates"
  cat > "$HONUA_GDAL_CANDIDATE_FILE" << 'EOF'
gdal-bin 3.8.4+dfsg-3ubuntu3
python3-gdal 3.8.4+dfsg-3ubuntu3
python3-numpy 1.26.4+ds-6ubuntu1
python3-scipy 1.11.4+dfsg-3ubuntu1
EOF
  key="$(build_version_key "$uris" "$manifest")"
  [[ "$key" == *"gdal-bin_3.8.4-dfsg-3ubuntu3"* ]] || {
    echo "version key lost the sanitized gdal-bin version: ${key}" >&2
    exit 1
  }
  [[ "$key" == *"python3-scipy_1.11.4-dfsg-3ubuntu1"* ]] || {
    echo "version key lost the sanitized scipy version: ${key}" >&2
    exit 1
  }
  [[ "$key" =~ ^[A-Za-z0-9._-]+$ ]] || {
    echo "version key is not cache-safe: ${key}" >&2
    exit 1
  }
  grep -qx 'gdal-bin_3.8.4+dfsg-3ubuntu3_amd64.deb' "$manifest" || {
    echo "manifest is missing the resolved gdal-bin deb" >&2
    exit 1
  }
  changed_uris="${root}/uris-changed"
  sed 's/gdal-bin_3.8.4/gdal-bin_3.8.5/g' "$uris" > "$changed_uris"
  changed_key="$(build_version_key "$changed_uris" "${root}/manifest-changed")"
  [[ "$changed_key" != "$key" ]] || {
    echo "a changed package filename left the version key unchanged" >&2
    exit 1
  }
  rm -rf "$root"
  unset HONUA_GDAL_CANDIDATE_FILE
}

prepare_stub_bin() {
  local bin=$1
  local name
  cat > "${bin}/sudo" << 'EOF'
#!/bin/bash
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
if [[ "$*" == *--download-only* ]]; then
  cp "${HONUA_DEB_FIXTURE_DIR:?}"/*.deb "${HONUA_APT_ARCHIVE_DIR:?}/"
fi
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
  local root bin archive cache summary path_file manifest fixture status
  root="$(mktemp -d)"
  bin="${root}/bin"
  archive="${root}/archives"
  cache="${root}/cache"
  summary="${root}/summary"
  path_file="${root}/github-path"
  manifest="${root}/runner/gdal-cli-debs.manifest"
  fixture="${root}/debs"
  mkdir -p "$bin" "$archive" "${root}/apt-conf" "$fixture" "${root}/runner"
  prepare_stub_bin "$bin"
  printf 'deb\n' > "${fixture}/gdal-bin_3.8.4+dfsg-3ubuntu3_amd64.deb"
  printf 'gdal-bin_3.8.4+dfsg-3ubuntu3_amd64.deb\n' > "$manifest"
  : > "${root}/apt.log"
  if [[ "$mode" == "hit" || "$mode" == "missing-tool" || "$mode" == "fallback" ]]; then
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
  set +e
  (
    set -euo pipefail
    export PATH="${bin}:${PATH}"
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
      miss)
        export GDAL_INDEX_STATE=ok
        export GDAL_DEB_CACHE_HIT=false
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
    hit|fallback)
      [[ "$status" -eq 0 ]] || { echo "${mode} install failed" >&2; exit 1; }
      grep -q -- '--no-download' "${root}/apt.log" || { echo "${mode} did not install offline" >&2; exit 1; }
      if grep -Eq 'update|--download-only' "${root}/apt.log"; then
        echo "${mode} reached the apt mirror: $(cat "${root}/apt.log")" >&2
        exit 1
      fi
      [[ -f "${archive}/gdal-bin_3.8.4+dfsg-3ubuntu3_amd64.deb" ]] || {
        echo "${mode} did not install the saved deb" >&2
        exit 1
      }
      grep -qx '/usr/bin' "$path_file" || { echo "${mode} did not publish /usr/bin" >&2; exit 1; }
      if [[ "$mode" == "hit" ]]; then
        grep -q 'Cache: hit' "$summary" || { echo "cache hit did not record the summary" >&2; exit 1; }
      else
        grep -q 'Cache: saved-image' "$summary" || { echo "saved-image install did not record the summary" >&2; exit 1; }
      fi
      ;;
    miss)
      [[ "$status" -eq 0 ]] || { echo "cache miss install failed" >&2; exit 1; }
      grep -q 'update' "${root}/apt.log" || { echo "cache miss did not update the package index" >&2; exit 1; }
      grep -q -- '--download-only' "${root}/apt.log" || { echo "cache miss did not download" >&2; exit 1; }
      grep -q -- '--no-download' "${root}/apt.log" || { echo "cache miss did not install offline" >&2; exit 1; }
      [[ -f "${cache}/gdal-bin_3.8.4+dfsg-3ubuntu3_amd64.deb" ]] || {
        echo "cache miss did not save the downloaded deb" >&2
        exit 1
      }
      grep -q 'Cache: miss' "$summary" || { echo "cache miss did not record the summary" >&2; exit 1; }
      ;;
    empty-hit|empty-fallback|missing-tool)
      [[ "$status" -ne 0 ]] || { echo "${mode} was accepted" >&2; exit 1; }
      ;;
    *)
      echo "unknown install case ${mode}" >&2
      exit 1
      ;;
  esac
  rm -rf "$root"
}

assert_resolve_unavailable() {
  local failure=$1
  local root bin output status updates
  root="$(mktemp -d)"
  bin="${root}/bin"
  mkdir -p "$bin" "${root}/apt-conf" "${root}/runner"
  prepare_stub_bin "$bin"
  if [[ "$failure" == "update" ]]; then
    cat > "${bin}/apt-get" << 'EOF'
#!/bin/bash
printf '%s\n' "$*" >> "${HONUA_APT_LOG:?}"
exit 1
EOF
  else
    cat > "${bin}/apt-get" << 'EOF'
#!/bin/bash
printf '%s\n' "$*" >> "${HONUA_APT_LOG:?}"
if [[ "$1" == "update" ]]; then
  exit 0
fi
exit 1
EOF
  fi
  chmod +x "${bin}/apt-get"
  output="${root}/github-output"
  : > "${root}/apt.log"
  : > "$output"
  : > "${root}/summary"
  set +e
  (
    set -euo pipefail
    export PATH="${bin}:${PATH}"
    export HONUA_APT_CONF_DIR="${root}/apt-conf"
    export HONUA_APT_LOG="${root}/apt.log"
    export GITHUB_OUTPUT="$output"
    export GITHUB_STEP_SUMMARY="${root}/summary"
    export RUNNER_TEMP="${root}/runner"
    export HONUA_GDAL_ERROR_MARK="provision-error: "
    export ImageOS="ubuntu24"
    export ImageVersion="20251101.1.0"
    cmd_resolve
  )
  status=$?
  set -e
  [[ "$status" -eq 0 ]] || {
    echo "unreadable index (${failure}) failed resolve with status ${status}" >&2
    exit 1
  }
  grep -qx 'index=unavailable' "$output" || {
    echo "resolve did not report an unreadable index (${failure})" >&2
    exit 1
  }
  grep -qx 'versions=unavailable' "$output" || {
    echo "resolve built a version key without an index (${failure})" >&2
    exit 1
  }
  grep -qx 'image=ubuntu24-20251101.1.0' "$output" || {
    echo "resolve dropped the runner image (${failure})" >&2
    exit 1
  }
  grep -qx 'cache-dir=.cache/gdal-cli-debs' "$output" || {
    echo "resolve dropped the cache directory (${failure})" >&2
    exit 1
  }
  if [[ "$failure" == "update" ]]; then
    if grep -q 'print-uris' "${root}/apt.log"; then
      echo "a failed index update still resolved package uris" >&2
      exit 1
    fi
  elif ! grep -q 'print-uris' "${root}/apt.log"; then
    echo "a failed uri listing never ran" >&2
    exit 1
  fi
  updates="$(grep -c 'update -q' "${root}/apt.log" || true)"
  if [[ "$failure" == "update" && "$updates" -ne 3 ]]; then
    echo "index update retried ${updates} times, expected 3" >&2
    exit 1
  fi
  if [[ "$failure" == "print-uris" && "$updates" -ne 1 ]]; then
    echo "a readable index updated ${updates} times" >&2
    exit 1
  fi
  rm -rf "$root"
}

assert_version_key
assert_resolve_unavailable update
assert_resolve_unavailable print-uris
run_install_case hit
run_install_case miss
run_install_case empty-hit
run_install_case empty-fallback
run_install_case fallback
run_install_case missing-tool
echo "provision-gdal-cli-self-test=ok"
