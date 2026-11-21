#!/usr/bin/env bash
# Refreshes collation versions on an existing data directory, then hands over to the official
# entrypoint unchanged. The environment is prepared the way the official entrypoint prepares it
# (as root, so secret files only root can read still work), but in a background subshell, so the
# official entrypoint later starts from the same environment as without this wrapper.
set -Eeuo pipefail
source /usr/local/bin/docker-entrypoint.sh

refresh() {
  docker_setup_env
  if [ "$(id -u)" = 0 ]; then
    docker_create_db_directories
  fi
  if [ -n "${DATABASE_ALREADY_EXISTS:-}" ]; then
    unset POSTGRES_PASSWORD
    export PGDATA POSTGRES_USER
    if [ "$(id -u)" = 0 ]; then
      gosu postgres honua-collation-refresh.sh "$@"
    else
      honua-collation-refresh.sh "$@"
    fi
  fi
}

# This shell is PID 1 while the refresh runs, so it receives the container's stop signal: stop the
# temporary server cleanly rather than leave it to be killed.
stop_refresh_server() {
  interrupted=1
  if [ "$(id -u)" = 0 ]; then
    gosu postgres pg_ctl -D "$PGDATA" -m fast -w stop >/dev/null 2>&1 || true
  else
    pg_ctl -D "$PGDATA" -m fast -w stop >/dev/null 2>&1 || true
  fi
}

first="${1:-}"
if [ "$first" != "${first#-}" ]; then
  set -- postgres "$@"
fi
if [ "${1:-}" = postgres ] && ! _pg_want_help "$@"; then
  interrupted=
  (refresh "$@") &
  refresh_pid=$!
  trap stop_refresh_server INT TERM
  set +e
  wait "$refresh_pid"
  status=$?
  while kill -0 "$refresh_pid" 2>/dev/null; do
    wait "$refresh_pid"
    status=$?
  done
  set -e
  trap - INT TERM
  if [ -n "$interrupted" ]; then
    exit 143
  fi
  if [ "$status" != 0 ]; then
    exit "$status"
  fi
fi
exec docker-entrypoint.sh "$@"
