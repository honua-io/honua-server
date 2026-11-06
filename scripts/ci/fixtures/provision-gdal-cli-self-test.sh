#!/usr/bin/env bash
# Exercise the GDAL CLI provisioner without apt, sudo, or the network.

set -euo pipefail

fixture_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "${fixture_dir}/../provision-gdal-cli.sh"
source "${fixture_dir}/provision-gdal-cli-install-cases.sh"

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
  updates="$(grep -c 'update --error-on=any -q' "${root}/apt.log" || true)"
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

assert_resolve_listing() {
  local kind=$1
  local root bin output status
  root="$(mktemp -d)"
  bin="${root}/bin"
  mkdir -p "$bin" "${root}/apt-conf" "${root}/runner"
  prepare_stub_bin "$bin"
  cat > "${root}/candidates" << 'EOF'
gdal-bin 3.8.4+dfsg-3ubuntu3
python3-gdal 3.8.4+dfsg-3ubuntu3
python3-numpy 1.26.4+ds-6ubuntu1
python3-scipy 1.11.4+dfsg-3ubuntu1
EOF
  if [[ "$kind" == "installed" ]]; then
    printf '%s\n' 'gdal-bin is already the newest version (3.8.4+dfsg-3ubuntu3)' > "${root}/uris"
  else
    printf '%s\n' "'http://example.invalid/pool/not-quoted.deb' broken" > "${root}/uris"
  fi
  cat > "${bin}/apt-get" << 'EOF'
#!/bin/bash
printf '%s\n' "$*" >> "${HONUA_APT_LOG:?}"
if [[ "$1" == "update" ]]; then
  exit 0
fi
if [[ "$*" == *--print-uris* ]]; then
  cat "${HONUA_PRINT_URIS:?}"
  exit 0
fi
exit 1
EOF
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
    export HONUA_PRINT_URIS="${root}/uris"
    export HONUA_GDAL_CANDIDATE_FILE="${root}/candidates"
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
  if [[ "$kind" == "installed" ]]; then
    [[ "$status" -eq 0 ]] || { echo "already-installed resolve failed" >&2; exit 1; }
    grep -qx 'index=ok' "$output" || { echo "already-installed resolve did not report a readable index" >&2; exit 1; }
    grep 'versions=' "$output" | grep -q 'gdal-bin_3.8.4-dfsg-3ubuntu3' || {
      echo "already-installed resolve dropped the candidate version" >&2
      exit 1
    }
    [[ ! -s "${root}/runner/gdal-cli-debs.manifest" ]] || {
      echo "already-installed resolve saved download filenames" >&2
      exit 1
    }
  else
    [[ "$status" -ne 0 ]] || { echo "malformed package listing was accepted" >&2; exit 1; }
    if grep -qx 'index=ok' "$output"; then
      echo "malformed package listing reported a readable index" >&2
      exit 1
    fi
  fi
  rm -rf "$root"
}

assert_version_key
assert_resolve_unavailable update
assert_resolve_unavailable print-uris
assert_resolve_listing installed
assert_resolve_listing malformed
run_install_case hit
run_install_case miss
run_install_case unset-miss
run_install_case partial-miss
run_install_case extra-archive
run_install_case empty-hit
run_install_case empty-fallback
run_install_case fallback
run_install_case hit-install-fails
run_install_case missing-tool
echo "provision-gdal-cli-self-test=ok"
