#!/usr/bin/env bash
#
# dotnet-restore-retry.sh — resilient `dotnet restore` wrapper for CI.
#
# Why this exists
# ---------------
# Every server-test / build / publish job in ci.yml runs `dotnet restore`
# before its build. Transient NuGet feed hiccups were ejecting otherwise-good
# PRs from the ALLGREEN merge queue (the whole merge_group is tested as one
# batch, so a single job's restore failure fails the entire batch): a one-off
# network/feed timeout or 5xx from api.nuget.org or the GitHub Packages feed
# during restore, where re-running the same restore succeeds. This wrapper
# retries restore a bounded number of times with a growing delay and turns on
# NuGet's enhanced HTTP retry so a single flaky request no longer fails the job.
#
# NuGet audit advisories (NU1901-NU1904) are not restore failures at all:
# Directory.Build.props lists them in WarningsNotAsErrors, so a freshly
# published advisory shows in the restore log without failing the job.
#
# Retries are bounded (HONUA_RESTORE_MAX_ATTEMPTS, default 3) and every attempt
# is logged, so a persistent restore failure still fails the job with clear
# output after N attempts instead of being masked.
#
# Usage:
#   scripts/ci/dotnet-restore-retry.sh <restore-target> [extra dotnet restore args...]
#
# Examples:
#   scripts/ci/dotnet-restore-retry.sh Honua.sln
#   scripts/ci/dotnet-restore-retry.sh src/Honua.Server/Honua.Server.csproj
#
set -euo pipefail

if [ "$#" -lt 1 ]; then
  echo "::error::dotnet-restore-retry.sh requires a restore target (solution or project)." >&2
  echo "usage: $0 <restore-target> [extra dotnet restore args...]" >&2
  exit 2
fi

target="$1"
shift

max_attempts="${HONUA_RESTORE_MAX_ATTEMPTS:-3}"
base_delay="${HONUA_RESTORE_RETRY_DELAY_SECONDS:-10}"

# Enhanced HTTP retry: NuGet retries transient HTTP failures (timeouts, 5xx,
# connection resets) inside a single restore before the request is considered
# failed. Combined with the outer retry loop this gives two layers of defence
# against feed flakiness.
export NUGET_ENABLE_ENHANCED_HTTP_RETRY="${NUGET_ENABLE_ENHANCED_HTTP_RETRY:-true}"
export NUGET_ENHANCED_MAX_NETWORK_TRY_COUNT="${NUGET_ENHANCED_MAX_NETWORK_TRY_COUNT:-6}"
export NUGET_ENHANCED_NETWORK_RETRY_DELAY_MILLISECONDS="${NUGET_ENHANCED_NETWORK_RETRY_DELAY_MILLISECONDS:-1000}"
# Bound any single hung request so a stuck feed connection cannot eat the whole
# job timeout before the outer retry loop gets a chance to re-issue restore.
export NUGET_HTTP_REQUEST_TIMEOUT="${NUGET_HTTP_REQUEST_TIMEOUT:-300}"

attempt=1
while true; do
  echo "::group::dotnet restore ${target} (attempt ${attempt}/${max_attempts})"
  set +e
  dotnet restore "${target}" "$@"
  status="$?"
  set -e
  echo "::endgroup::"

  if [ "${status}" -eq 0 ]; then
    exit 0
  fi

  if [ "${attempt}" -ge "${max_attempts}" ]; then
    echo "::error::dotnet restore failed after ${max_attempts} attempts (last exit code ${status})." >&2
    exit "${status}"
  fi

  delay=$(( base_delay * attempt ))
  echo "::warning::dotnet restore attempt ${attempt}/${max_attempts} failed (exit ${status}); retrying in ${delay}s — likely a transient NuGet feed hiccup." >&2
  sleep "${delay}"
  attempt=$(( attempt + 1 ))
done
