#!/usr/bin/env bash
set -euo pipefail

COMPOSE_FILE="infrastructure/samples/docker-compose/docker-compose.yml"
PROJECT_NAME="honua-iac-sample"
POSTGRES_HEALTHCHECK_TIMEOUT=120
REDIS_HEALTHCHECK_TIMEOUT=120
HONUA_HEALTHCHECK_TIMEOUT=300
CLEANUP=true
INTERACTIVE=false
BUILD=true
HONUA_HTTP_PORT="${HONUA_HTTP_PORT:-8080}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --no-cleanup)
      CLEANUP=false
      shift
      ;;
    --interactive)
      INTERACTIVE=true
      shift
      ;;
    --no-build)
      BUILD=false
      shift
      ;;
    --help|-h)
      echo "Usage: $0 [--no-cleanup] [--interactive] [--no-build]"
      exit 0
      ;;
    *)
      echo "Unknown option: $1"
      exit 1
      ;;
  esac
done

if ! command -v docker >/dev/null 2>&1; then
  echo "Docker not found. Please install Docker."
  exit 1
fi

if ! command -v docker-compose >/dev/null 2>&1 && ! command -v docker compose >/dev/null 2>&1; then
  echo "Docker Compose not found. Please install Docker Compose."
  exit 1
fi

if command -v docker-compose >/dev/null 2>&1; then
  COMPOSE_CMD="docker-compose"
else
  COMPOSE_CMD="docker compose"
fi

if ! command -v curl >/dev/null 2>&1; then
  echo "curl not found. Please install curl."
  exit 1
fi

cleanup() {
  if [[ "$CLEANUP" == "true" && "$INTERACTIVE" == "false" ]]; then
    $COMPOSE_CMD -f "$COMPOSE_FILE" -p "$PROJECT_NAME" down --remove-orphans --volumes >/dev/null 2>&1 || true
  fi
}

trap cleanup EXIT

$COMPOSE_CMD -f "$COMPOSE_FILE" -p "$PROJECT_NAME" down --remove-orphans --volumes >/dev/null 2>&1 || true

BUILD_FLAG=""
if [[ "$BUILD" == "true" ]]; then
  if grep -q "^[[:space:]]*build:" "$COMPOSE_FILE"; then
    BUILD_FLAG="--build"
  fi
fi

$COMPOSE_CMD -f "$COMPOSE_FILE" -p "$PROJECT_NAME" up -d $BUILD_FLAG

postgres_id=$($COMPOSE_CMD -f "$COMPOSE_FILE" -p "$PROJECT_NAME" ps -q postgres)
if [[ -z "$postgres_id" ]]; then
  echo "Postgres container not found."
  exit 1
fi

start_time=$(date +%s)
while true; do
  status=$(docker inspect -f '{{.State.Health.Status}}' "$postgres_id" 2>/dev/null || echo "starting")
  if [[ "$status" == "healthy" ]]; then
    break
  fi
  if [[ "$status" == "unhealthy" ]]; then
    echo "Postgres container is unhealthy."
    $COMPOSE_CMD -f "$COMPOSE_FILE" -p "$PROJECT_NAME" logs postgres
    exit 1
  fi
  if (( $(date +%s) - start_time > POSTGRES_HEALTHCHECK_TIMEOUT )); then
    echo "Timeout waiting for Postgres health check."
    $COMPOSE_CMD -f "$COMPOSE_FILE" -p "$PROJECT_NAME" logs postgres
    exit 1
  fi
  sleep 5
done

redis_id=$($COMPOSE_CMD -f "$COMPOSE_FILE" -p "$PROJECT_NAME" ps -q redis)
if [[ -z "$redis_id" ]]; then
  echo "Redis container not found."
  exit 1
fi

start_time=$(date +%s)
while true; do
  status=$(docker inspect -f '{{.State.Health.Status}}' "$redis_id" 2>/dev/null || echo "starting")
  if [[ "$status" == "healthy" ]]; then
    break
  fi
  if [[ "$status" == "unhealthy" ]]; then
    echo "Redis container is unhealthy."
    $COMPOSE_CMD -f "$COMPOSE_FILE" -p "$PROJECT_NAME" logs redis
    exit 1
  fi
  if (( $(date +%s) - start_time > REDIS_HEALTHCHECK_TIMEOUT )); then
    echo "Timeout waiting for Redis health check."
    $COMPOSE_CMD -f "$COMPOSE_FILE" -p "$PROJECT_NAME" logs redis
    exit 1
  fi
  sleep 5
done

start_time=$(date +%s)
while true; do
  if curl -fsS "http://localhost:${HONUA_HTTP_PORT}/healthz/live" >/dev/null; then
    break
  fi
  if (( $(date +%s) - start_time > HONUA_HEALTHCHECK_TIMEOUT )); then
    echo "Timeout waiting for Honua health check."
    $COMPOSE_CMD -f "$COMPOSE_FILE" -p "$PROJECT_NAME" logs honua
    exit 1
  fi
  sleep 5
done

echo "Docker Compose sample is healthy."

if [[ "$INTERACTIVE" == "true" ]]; then
  echo "Containers left running (project: $PROJECT_NAME)."
fi
