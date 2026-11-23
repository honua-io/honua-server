#!/bin/sh
set -eu
root=$1
# Follow the .NET resolute-chiseled-extra slice set and Chisel toolchain:
# https://github.com/dotnet/dotnet-docker/tree/main/src/runtime-deps/11.0/resolute-chiseled-extra
case "$(dpkg --print-architecture)" in
    amd64) checksum=f8ba3f3d04165b50d91aae7eb3bcaca2a714f1877d2af005989617937cf2e375ab30d4a50b1308263e61a61ecb79de5c ;;
    arm64) checksum=90d2f15242594a20d634a66ed7a2bee7586cfd83498f42b284ca2dcb6ccd29772230172604881e168b5fc12ffd8dda91 ;;
    *) echo "Unsupported runtime architecture" >&2; exit 1 ;;
esac
archive=$(mktemp)
trap 'rm -f "$archive"' EXIT
curl --fail --show-error --location --output "$archive" \
    "https://github.com/canonical/chisel/releases/download/v1.5.0/chisel_v1.5.0_linux_$(dpkg --print-architecture).tar.gz"
printf '%s  %s\n' "$checksum" "$archive" | sha384sum -c -
tar --gzip --extract --no-same-owner --file "$archive" --directory /usr/local/bin/ chisel
curl --fail --show-error --location --output /usr/local/bin/chisel-wrapper \
    https://raw.githubusercontent.com/canonical/rocks-toolbox/v1.2.0/chisel-wrapper
printf '%s  %s\n' 94f97c7624fbaf84a747e7456726abaf1b5134e75197cfe6f39bd62406714de4 \
    /usr/local/bin/chisel-wrapper | sha256sum -c -
chmod 755 /usr/local/bin/chisel-wrapper
# Same cache-layout workaround as the publisher: upstream chisel-wrapper
# reads only chisel/sha256, but the chisel cache keeps blobs under chisel/sha*/.
sed --in-place \
    -e 's|dir="$CHISEL_CACHE_DIR/chisel/sha256"|dir="$CHISEL_CACHE_DIR/chisel"|' \
    -e 's|for f in "$dir"/\*; do|for f in "$dir"/sha*/*; do|' \
    /usr/local/bin/chisel-wrapper
mkdir -p "$root/var/lib/dpkg"
chisel-wrapper --generate-dpkg-status "$root/var/lib/dpkg/status" -- \
    --release ubuntu-26.04 --ignore=unstable --root "$root" \
    base-files_base base-files_chisel base-files_release-info ca-certificates_data \
    libc6_libs libgcc-s1_libs libicu78_libs libssl3t64_libs libstdc++6_libs \
    tzdata-legacy_zoneinfo tzdata_zoneinfo
test -s "$root/var/lib/dpkg/status"
# Preserve the publisher's launcher layout; Docker COPY dereferences symlinks.
mkdir -p "$root/usr/bin"
ln -s ../share/dotnet/dotnet "$root/usr/bin/dotnet"
