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

dotnet restore "$project" --configfile "$nuget_config" "$@"
