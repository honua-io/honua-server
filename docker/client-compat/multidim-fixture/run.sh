#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
compose_file="$repo_root/docker/client-compat/compose.yml"

export HONUA_CLIENT_COMPAT_ENVIRONMENT=Development
export HONUA_CLIENT_COMPAT_DEV_GRANT_EDITION=Pro
export HONUA_CLIENT_COMPAT_STORAGE_PROVIDER=AwsS3

docker compose -f "$compose_file" --profile multidim-fixture up \
  --build \
  --wait \
  honua gdal-worker

docker compose -f "$compose_file" --profile multidim-fixture run \
  --build \
  --rm \
  --no-deps \
  multidim-fixture
