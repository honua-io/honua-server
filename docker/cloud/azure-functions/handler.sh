#!/bin/sh

set -eu

port="${FUNCTIONS_CUSTOMHANDLER_PORT:-8080}"

export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}"
export ASPNETCORE_URLS="http://0.0.0.0:${port}"
export DOTNET_RUNNING_IN_CONTAINER=true
export DOTNET_EnableDiagnostics="${DOTNET_EnableDiagnostics:-0}"

# The Functions host starts this handler from the script root (/home/site/wwwroot) and may pass
# its own ASPNETCORE_CONTENTROOT; pin the content root to the app so appsettings.json is found
# (without it, startup validation fails on code defaults such as AdaptiveSampling thresholds).
app_dir=/home/site/wwwroot/app
export ASPNETCORE_CONTENTROOT="$app_dir"
cd "$app_dir"

exec "$app_dir/Honua.Server"
