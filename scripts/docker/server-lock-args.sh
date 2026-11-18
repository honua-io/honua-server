#!/bin/sh
# Prints the MSBuild argument that makes a shipped Honua.Server image restore in NuGet
# locked mode from eng/nuget-locks/ (see eng/Honua.ServerLockFiles.props), or nothing
# for a custom build with other profiles or provider flags.
# usage: server-lock-args.sh <serving|aot|lambda|lambda-aot>
# Reads HONUA_STORE_PROFILE, HONUA_BUILD_PROFILE, HONUA_PROTOCOL_PROFILE and HONUA_INCLUDE_*.
set -eu

if [ "$#" -ne 1 ]; then
  echo "usage: $0 <serving|aot|lambda|lambda-aot>" >&2
  exit 2
fi

case "$1" in
  serving|aot) variant="$1-${HONUA_STORE_PROFILE:-postgis}" ;;
  lambda|lambda-aot) variant="$1" ;;
  *) echo "unknown image kind: $1" >&2; exit 2 ;;
esac

custom=""
[ "${HONUA_BUILD_PROFILE:-full}" = full ] || custom=1
[ "${HONUA_PROTOCOL_PROFILE:-all}" = all ] || custom=1
[ -z "${HONUA_INCLUDE_AWS:-}" ] || custom=1
[ -z "${HONUA_INCLUDE_AZURE:-}" ] || custom=1
case "${HONUA_INCLUDE_ORACLE:-}" in ''|false) ;; *) custom=1 ;; esac
case "${HONUA_INCLUDE_SNOWFLAKE:-}" in ''|false) ;; *) custom=1 ;; esac
case "$variant" in
  serving-postgis|serving-full|aot-postgis|lambda|lambda-aot) ;;
  *) custom=1 ;;
esac

if [ -n "$custom" ]; then
  echo "server-lock-args: custom build ($variant), restoring without a lock file" >&2
  exit 0
fi
printf -- '-p:HonuaLockVariant=%s\n' "$variant"
