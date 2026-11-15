#!/usr/bin/env bash
# Keep the secret-bearing startup configuration private and reusable for cleanup.
set -euo pipefail

: "${RUNNER_TEMP:?RUNNER_TEMP is required}"
project=honua-capacity-soak
private_dir="$RUNNER_TEMP/capacity-substrate"
prepared="$private_dir/compose.yaml"
container_file="$private_dir/server-container"

case "${1:-}" in
  start)
    umask 077
    mkdir -p "$private_dir"
    chmod 0700 "$private_dir"
    # Render before the first mutation. A partial `up` must leave the same inputs
    # available to `down`, even after the startup step environment disappears.
    docker compose --project-directory . --project-name "$project" -f docker/compose/docker-compose.soak.yml config > "$prepared.tmp"
    mv "$prepared.tmp" "$prepared"
    docker compose --project-name "$project" -f "$prepared" up -d
    container="$(docker compose --project-name "$project" -f "$prepared" ps -q honua)"
    newline='
'
    if [[ -z "$container" || "$container" == *"$newline"* ]]; then
      echo "::error::Expected exactly one capacity candidate container" >&2
      exit 1
    fi
    printf '%s\n' "$container" > "$container_file"
    ;;
  wait)
    container="$(cat "$container_file")"
    if ! python3 "${BASH_SOURCE[0]%/*}/../ci/owned-server.py" wait \
      --container "$container" --container-port 8080 \
      --url "${BASE_URL:?BASE_URL is required}/healthz/ready" \
      --timeout "${HONUA_SOAK_READY_SECONDS:-600}"; then
      docker logs --tail 120 "$container" || true
      exit 1
    fi
    ;;
  cleanup)
    cleanup_status=0
    if [[ -f "$prepared" ]]; then
      docker compose --project-name "$project" -f "$prepared" down -v --remove-orphans || cleanup_status=$?
    fi
    # Early failures may precede rendering. Confirm absence rather than inventing
    # dummy license/image inputs or silently accepting an unknown obligation.
    for kind in container network volume; do
      case "$kind" in
        container) remaining="$(docker ps --all --quiet --filter "label=com.docker.compose.project=$project")" ;;
        network) remaining="$(docker network ls --quiet --filter "label=com.docker.compose.project=$project")" ;;
        volume) remaining="$(docker volume ls --quiet --filter "label=com.docker.compose.project=$project")" ;;
      esac
      if [[ -n "$remaining" ]]; then
        echo "::error::Capacity substrate cleanup left project-owned $kind resources" >&2
        cleanup_status=1
      fi
    done
    if [[ "$cleanup_status" != 0 ]]; then
      echo "::error::Capacity substrate cleanup failed; retain the private configuration for recovery" >&2
      exit "$cleanup_status"
    fi
    rm -f "$prepared" "$prepared.tmp" "$container_file"
    ;;
  *) echo "Usage: $0 start|wait|cleanup" >&2; exit 2 ;;
esac
