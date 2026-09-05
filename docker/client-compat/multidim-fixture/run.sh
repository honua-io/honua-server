#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
compose_file="$repo_root/docker/client-compat/compose.yml"
build_secret_dir="$(mktemp -d)"
trap 'rm -rf "$build_secret_dir"' EXIT

github_packages_token="${HONUA_DOCKER_GITHUB_TOKEN:-${GITHUB_TOKEN:-${GH_TOKEN:-}}}"
github_packages_actor="${GITHUB_ACTOR:-${GH_USERNAME:-${USER:-honua}}}"
if [[ -z "$github_packages_token" ]]; then
  echo "GitHub Packages authentication is required to restore Geospatial.Grpc." >&2
  echo "Set HONUA_DOCKER_GITHUB_TOKEN, GITHUB_TOKEN, or GH_TOKEN." >&2
  exit 1
fi
printf '%s' "$github_packages_actor" > "$build_secret_dir/github-actor"
printf '%s' "$github_packages_token" > "$build_secret_dir/github-token"
export HONUA_GITHUB_ACTOR_SECRET_FILE="$build_secret_dir/github-actor"
export HONUA_GITHUB_TOKEN_SECRET_FILE="$build_secret_dir/github-token"

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
