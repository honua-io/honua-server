#!/usr/bin/env bash
set -euo pipefail

count_file="${HONUA_FAKE_GH_COUNT}"
count=0
[[ ! -f "${count_file}" ]] || count="$(<"${count_file}")"
count=$(( count + 1 ))
printf '%s\n' "${count}" > "${count_file}"
if (( count < HONUA_FAKE_GH_SUCCEED_AT )); then
  echo "artifact unavailable" >&2
  exit 1
fi
destination=""
while [[ $# -gt 0 ]]; do
  if [[ "$1" == "--dir" ]]; then destination="$2"; break; fi
  shift
done
mkdir -p "${destination}"
printf 'payload\n' > "${destination}/server-test-binaries-server.tar.gz"
