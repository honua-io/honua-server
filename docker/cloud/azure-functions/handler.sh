#!/bin/sh

set -eu

port="${FUNCTIONS_CUSTOMHANDLER_PORT:-8080}"

export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}"
export ASPNETCORE_URLS="http://0.0.0.0:${port}"
export DOTNET_RUNNING_IN_CONTAINER=true
export DOTNET_EnableDiagnostics="${DOTNET_EnableDiagnostics:-0}"

exec /home/site/wwwroot/app/Honua.Server
