#!/usr/bin/env bash
# Regenerates eng/nuget-locks/honua-server.<variant>.lock.json for every shipped
# Honua.Server image. Each variant restores with the same MSBuild properties as its
# Dockerfile restore step (Dockerfile, docker/Dockerfile.aot, docker/Dockerfile.lambda,
# docker/Dockerfile.lambda.aot), so a stale or mismatched lock fails the image build.
# Run on the build host: remote-run bash scripts/ci/regenerate-server-lock-files.sh [--check] [variant...]
# --check restores in locked mode instead and fails when a lock is stale.
set -euo pipefail

cd "$(dirname "$0")/../.."
project=src/Honua.Server/Honua.Server.csproj
read -r -a rids <<< "${HONUA_LOCK_RIDS:-linux-x64 linux-arm64}"
config_args=()
if [ -n "${HONUA_NUGET_CONFIG:-}" ]; then
  config_args=(--configfile "$HONUA_NUGET_CONFIG")
fi
profile_args=(-p:HonuaUbuntuRuntimePublish=true -p:HonuaUseSkiaNoDependencies=true -p:HonuaBuildProfile=full -p:HonuaProtocolProfile=all)

variant_args() {
  case "$1" in
    serving-postgis|serving-full)
      printf '%s\n' -p:HonuaSkipRepoAnalyzers=true -p:RunAnalyzersDuringBuild=false \
        "-p:HonuaStoreProfile=${1#serving-}" "${profile_args[@]}" ;;
    aot-postgis)
      printf '%s\n' -p:HonuaStoreProfile=postgis "${profile_args[@]}" \
        -p:HonuaIncludeOracle=false -p:HonuaIncludeSnowflake=false ;;
    lambda)
      printf '%s\n' -p:PublishAot=false -p:SelfContained=true ;;
    lambda-aot)
      printf '%s\n' -p:HonuaUseSkiaNoDependencies=true ;;
    *)
      echo "unknown variant: $1" >&2
      return 2 ;;
  esac
}

lock_mode=(-p:RestoreLockedMode=false -p:RestoreForceEvaluate=true)
if [ "${1:-}" = "--check" ]; then
  lock_mode=(-p:RestoreLockedMode=true)
  shift
fi

variants=("$@")
if [ "${#variants[@]}" -eq 0 ]; then
  variants=(serving-postgis serving-full aot-postgis lambda lambda-aot)
fi
for variant in "${variants[@]}"; do
  variant_args "$variant" > /dev/null
done

# The locks pin SDK-implied packages (ILCompiler, ILLink), so they must come from the SDK the
# images build with; a newer local SDK (global.json rolls forward) would write locks they reject.
image_sdk="$(sed -n 's|^ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:\([0-9.]*\)-.*|\1|p' Dockerfile)"
if [ "$(dotnet --version)" != "$image_sdk" ]; then
  echo "dotnet $(dotnet --version) differs from the image SDK $image_sdk; install SDK $image_sdk to regenerate." >&2
  exit 1
fi

for variant in "${variants[@]}"; do
  mapfile -t args < <(variant_args "$variant")
  for rid in "${rids[@]}"; do
    echo "== $variant $rid"
    dotnet restore "$project" "${config_args[@]}" --disable-parallel --runtime "$rid" \
      "-p:RuntimeIdentifier=$rid" \
      "-p:HonuaLockVariant=$variant" \
      "${lock_mode[@]}" \
      "${args[@]}"
  done
done
