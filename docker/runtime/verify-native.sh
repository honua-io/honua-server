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
for native in "$runtime_root$app_directory/"*.so "$runtime_root/app/honua-healthcheck"; do
    case "$native" in
        # The client selects a platform-specific native variant at runtime.
        */*librdkafka*.so) continue ;;
    esac
    printf 'Checking %s\n' "${native#"$runtime_root"}"
    chroot "$runtime_root" "$loader" --list "${native#"$runtime_root"}"
    case "$native" in
        # SQLite uses math symbols supplied by its embedding executable.
        */libe_sqlite3.so|*/honua-healthcheck) continue ;;
    esac
    linkage=$(LD_LIBRARY_PATH="$runtime_root/usr/lib/$(cc -print-multiarch)" ldd -r "$native" 2>&1)
    printf '%s\n' "$linkage"
    if printf '%s\n' "$linkage" | grep -Eq 'not found|undefined symbol|not a dynamic executable'; then
        exit 1
    fi
done
