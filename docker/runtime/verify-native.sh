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
cp "$(command -v env)" "$runtime_root/.verify-env"
trap 'rm -f "$runtime_root/.verify-env"' EXIT

verify_native() {
    native=$1
    printf 'Checking %s\n' "${native#"$runtime_root"}"
    chroot "$runtime_root" "$loader" --list "${native#"$runtime_root"}" || return 1
    case "$native" in
        # SQLite uses math symbols supplied by its embedding executable.
        */libe_sqlite3.so) return 0 ;;
    esac
    linkage=$(chroot "$runtime_root" /.verify-env LD_TRACE_LOADED_OBJECTS=1 LD_WARN=yes LD_BIND_NOW=yes \
        "$loader" "${native#"$runtime_root"}" 2>&1) || return 1
    printf '%s\n' "$linkage"
    if printf '%s\n' "$linkage" | grep -Eq 'not found|undefined symbol|not a dynamic executable'; then
        return 1
    fi
}

for native in "$runtime_root$app_directory/"*.so "$runtime_root/app/honua-healthcheck"; do
    case "$native" in
        # Only one platform variant is loaded; verify the available set below.
        */*-librdkafka.so) continue ;;
    esac
    verify_native "$native"
done

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
