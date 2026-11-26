#!/bin/sh
set -eu
runtime_root=$1
app_directory=$2
loader=$(find "$runtime_root/usr/lib" -name 'ld-linux*.so.*' -type f -print -quit)
test -n "$loader"
loader=${loader#"$runtime_root"}
test -r "$runtime_root/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
test -f "$runtime_root$app_directory/ParquetSharpNative.so"
test -f "$runtime_root$app_directory/libSkiaSharp.so"
cc -static -O2 -Wall -Wextra -Werror "$(dirname "$0")/verify-env.c" -o "$runtime_root/.verify-env"
trap 'rm -f "$runtime_root/.verify-env"' EXIT

verify_native() {
    native=$1
    printf 'Checking %s\n' "${native#"$runtime_root"}"
    chroot "$runtime_root" "$loader" --list "${native#"$runtime_root"}" || return 1
    case "$native" in
        # SQLite uses math symbols supplied by its embedding executable.
        */libe_sqlite3.so) return 0 ;;
    esac
    linkage=$(chroot "$runtime_root" /.verify-env "$loader" "${native#"$runtime_root"}" 2>&1) || return 1
    printf '%s\n' "$linkage"
    if printf '%s\n' "$linkage" | grep -Eq 'not found|undefined symbol|not a dynamic executable'; then
        return 1
    fi
}

for native in "$runtime_root$app_directory/"*.so "$runtime_root/app/honua-healthcheck" "$runtime_root/app/honua-entrypoint"; do
    case "$native" in
        # Only one platform variant is loaded; verify the available set below.
        */*-librdkafka.so) continue ;;
    esac
    verify_native "$native"
done

verify_arm64_branch_range() {
    executable=$1
    test -f "$executable" || return 0
    readelf -h "$executable" 2>/dev/null | grep -Eq 'Machine:[[:space:]]+AArch64' || return 0
    managed_hex=$(readelf -SW "$executable" | awk '{ sub(/^ *\[[ 0-9]+\] */, ""); if ($1 == "__managedcode") { print $5; exit } }')
    test -n "$managed_hex" || return 0
    managed_bytes=$((0x$managed_hex))
    # ARM64 B/BL immediate range is +/-128 MiB; ilc pre-resolves intra-__managedcode branches without thunks.
    arm64_branch_limit=$((128 * 1024 * 1024))
    printf 'Checking %s __managedcode size: %d bytes (limit %d)\n' \
        "${executable#"$runtime_root"}" "$managed_bytes" "$arm64_branch_limit"
    if [ "$managed_bytes" -ge "$arm64_branch_limit" ]; then
        printf 'ARM64 __managedcode (%d bytes) exceeds +/-128 MiB B/BL branch range (%d bytes)\n' \
            "$managed_bytes" "$arm64_branch_limit" >&2
        return 1
    fi
}

if [ ! -f "$runtime_root$app_directory/librdkafka.so" ]; then
    client_variant_present=false
    compatible_variant_found=false
    for candidate in "$runtime_root$app_directory/"*-librdkafka.so; do
        test -f "$candidate" || continue
        client_variant_present=true
        if verify_native "$candidate"; then
            compatible_variant_found=true
            break
        fi
    done
    if [ "$client_variant_present" = true ] || [ -f "$runtime_root$app_directory/Confluent.Kafka.dll" ]; then
        test "$compatible_variant_found" = true || { echo 'No compatible client native variant' >&2; exit 1; }
    fi
fi

verify_arm64_branch_range "$runtime_root$app_directory/Honua.Server"
