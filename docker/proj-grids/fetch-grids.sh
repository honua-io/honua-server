#!/usr/bin/env bash
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
#
# Fetch the PROJ grid files listed in grids.txt from the PROJ CDN into the PROJ
# data directory (issue #1501). Used at image-build time by Dockerfile, and also
# runnable against a live PROJ data path for local provisioning.
#
# Usage:
#   fetch-grids.sh <manifest> <proj-data-dir>
#
# The manifest is grids.txt (one canonical PROJ grid filename per line; '#'
# comments and blank lines ignored). Each grid is downloaded from
# ${PROJ_CDN_BASE:-https://cdn.proj.org}. The download fails loudly (non-zero
# exit) if any grid is missing or truncated, so a broken image is never produced.

set -euo pipefail

MANIFEST="${1:?usage: fetch-grids.sh <manifest> <proj-data-dir>}"
PROJ_DATA_DIR="${2:?usage: fetch-grids.sh <manifest> <proj-data-dir>}"
CDN_BASE="${PROJ_CDN_BASE:-https://cdn.proj.org}"

mkdir -p "$PROJ_DATA_DIR"

count=0
while IFS= read -r raw_line || [ -n "$raw_line" ]; do
    # Strip comments and surrounding whitespace.
    line="${raw_line%%#*}"
    line="$(printf '%s' "$line" | tr -d '[:space:]')"
    [ -z "$line" ] && continue

    url="${CDN_BASE}/${line}"
    dest="${PROJ_DATA_DIR}/${line}"
    echo "Fetching ${url}"
    # --fail: error on HTTP >= 400; -L: follow redirects; -sS: quiet but show errors.
    if ! curl --fail -sSL -o "$dest" "$url"; then
        echo "ERROR: failed to download grid '${line}' from ${url}" >&2
        exit 1
    fi
    if [ ! -s "$dest" ]; then
        echo "ERROR: downloaded grid '${line}' is empty" >&2
        exit 1
    fi
    count=$((count + 1))
done < "$MANIFEST"

echo "Provisioned ${count} PROJ grid file(s) into ${PROJ_DATA_DIR}"
