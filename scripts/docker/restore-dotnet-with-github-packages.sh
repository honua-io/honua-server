#!/bin/sh
set -eu

if [ "$#" -lt 1 ]; then
  echo "usage: $0 <project-or-solution> [dotnet restore args...]" >&2
  exit 2
fi

project="$1"
shift

nuget_config="/tmp/honua-nuget.config"
cp NuGet.config "$nuget_config"
trap 'rm -f "$nuget_config"' EXIT

# Make restore resilient to transient GitHub Packages / nuget.org blips. These
# only affect network fetches; on a warm cache they are no-ops. Tune via env.
export NUGET_ENABLE_ENHANCED_HTTP_RETRY="${NUGET_ENABLE_ENHANCED_HTTP_RETRY:-true}"
export NUGET_ENHANCED_MAX_NETWORK_TRY_COUNT="${NUGET_ENHANCED_MAX_NETWORK_TRY_COUNT:-6}"
export NUGET_ENHANCED_NETWORK_RETRY_DELAY_MILLISECONDS="${NUGET_ENHANCED_NETWORK_RETRY_DELAY_MILLISECONDS:-1000}"

if [ -s /run/secrets/github_token ]; then
  github_actor="github-actions"
  if [ -s /run/secrets/github_actor ]; then
    github_actor="$(cat /run/secrets/github_actor)"
  fi

  github_token="$(cat /run/secrets/github_token)"
  dotnet nuget update source github-honua \
    --configfile "$nuget_config" \
    --username "$github_actor" \
    --password "$github_token" \
    --store-password-in-clear-text >/dev/null
fi

# --disable-parallel serializes per-project restore so the project graph's
# obj/project.assets.json files are written deterministically. Parallel restore
# of this wide graph intermittently raced and left a project without its assets
# file, surfacing later as NETSDK1004 ("Assets file ... not found") in the
# --no-restore publish that follows.
#
# Retry the whole `dotnet restore` invocation on failure. NUGET_ENABLE_ENHANCED_HTTP_RETRY
# above retries transient network/5xx blips *within* a single restore, but it does NOT
# retry hard auth/availability failures from GitHub Packages — e.g. NU1301 with
# "Response status code does not indicate success: 401 (Unauthorized)", which GitHub
# Packages intermittently returns under load even with a valid token. Those need a
# fresh restore invocation, so we loop with linear backoff. On a warm cache this
# succeeds on the first try and the loop is a no-op. Tune via env.
restore_max_attempts="${HONUA_RESTORE_MAX_ATTEMPTS:-5}"
restore_retry_delay="${HONUA_RESTORE_RETRY_DELAY_SECONDS:-10}"

attempt=1
while :; do
  if dotnet restore "$project" --configfile "$nuget_config" --disable-parallel "$@"; then
    break
  fi

  if [ "$attempt" -ge "$restore_max_attempts" ]; then
    echo "dotnet restore failed after ${attempt} attempt(s); giving up." >&2
    exit 1
  fi

  delay=$((restore_retry_delay * attempt))
  echo "dotnet restore failed (attempt ${attempt}/${restore_max_attempts}); retrying in ${delay}s..." >&2
  sleep "$delay"
  attempt=$((attempt + 1))
done
