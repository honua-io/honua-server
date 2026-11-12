#!/usr/bin/env bash
# Print the target framework every Honua project builds for: the HonuaTargetFramework
# environment variable when set, otherwise its default in Directory.Build.props. MSBuild reads the same
# environment variable as the property, so one setting moves both the build and these output paths
# (src/Honua.Server/bin/Release/<tfm>/...).
set -euo pipefail

if [[ -n "${HonuaTargetFramework:-}" ]]; then
  printf '%s\n' "${HonuaTargetFramework}"
  exit 0
fi

repo_root="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
tfm="$(sed -n 's#.*<HonuaTargetFramework Condition="[^"]*">\([^<]*\)</HonuaTargetFramework>.*#\1#p' \
  "${repo_root}/Directory.Build.props" | head -n 1)"
if [[ -z "${tfm}" ]]; then
  echo "honua-target-framework: no HonuaTargetFramework default in ${repo_root}/Directory.Build.props" >&2
  exit 1
fi
printf '%s\n' "${tfm}"
