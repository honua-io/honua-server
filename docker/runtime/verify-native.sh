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
for native in "$runtime_root$app_directory/"*.so "$runtime_root/app/honua-healthcheck"; do
    case "$native" in
        # The client selects a platform-specific native variant at runtime.
        */*librdkafka*.so) continue ;;
    esac
    printf 'Checking %s\n' "${native#"$runtime_root"}"
    chroot "$runtime_root" "$loader" --list "${native#"$runtime_root"}"
    case "$native" in
        # SQLite uses math symbols supplied by its embedding executable.
        */libe_sqlite3.so) continue ;;
    esac
    linkage=$(chroot "$runtime_root" /.verify-env LD_TRACE_LOADED_OBJECTS=1 LD_WARN=yes LD_BIND_NOW=yes \
        "$loader" "${native#"$runtime_root"}" 2>&1)
    printf '%s\n' "$linkage"
    if printf '%s\n' "$linkage" | grep -Eq 'not found|undefined symbol|not a dynamic executable'; then
        exit 1
    fi
done
