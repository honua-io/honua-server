#!/usr/bin/env bash
set -euo pipefail

docker_args=()
github_token="${GITHUB_TOKEN:-${GH_TOKEN:-}}"

if [[ -n "$github_token" ]]; then
  export HONUA_DOCKER_GITHUB_TOKEN="$github_token"
  export GITHUB_ACTOR="${GITHUB_ACTOR:-github-actions}"
  docker_args+=(
    --secret id=github_actor,env=GITHUB_ACTOR
    --secret id=github_token,env=HONUA_DOCKER_GITHUB_TOKEN
  )
else
  echo "warning: GITHUB_TOKEN or GH_TOKEN is not set; GitHub Packages restore may fail." >&2
fi

DOCKER_BUILDKIT="${DOCKER_BUILDKIT:-1}" docker build "${docker_args[@]}" "$@"
