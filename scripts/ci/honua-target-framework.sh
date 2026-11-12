#!/usr/bin/env bash
# Print the target framework every Honua project builds for (#7380): HONUA_TARGET_FRAMEWORK when set,
# otherwise the HonuaTargetFramework default in Directory.Build.props. Use it to derive output paths
# (src/Honua.Server/bin/Release/<tfm>/...) instead of hard-coding a framework.
set -euo pipefail

if [[ -n "${HONUA_TARGET_FRAMEWORK:-}" ]]; then
  printf '%s\n' "${HONUA_TARGET_FRAMEWORK}"
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
