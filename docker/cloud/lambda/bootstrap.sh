#!/bin/sh
set -eu

export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://0.0.0.0:${PORT:-8080}}"

exec /var/task/Honua.Server
