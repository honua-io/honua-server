#!/bin/sh
set -eu
runtime_root=$1
additions=$2
packages=$(mktemp)
trap 'rm -f "$packages"' EXIT

mkdir -p "$additions/app" "$additions/usr/share/fonts/truetype"
for library in /usr/lib/*/libatomic.so.1 /usr/lib/*/libgssapi_krb5.so.2; do
    {
        printf '%s\n' "$library"
        ldd "$library" | awk '/=> \// {print $3}'
    } | while IFS= read -r dependency; do
        directory=$(dirname "$(readlink -f "$dependency")")
        target="$directory/$(basename "$dependency")"
        if [ ! -e "$runtime_root$target" ]; then
            mkdir -p "$additions$directory"
            cp -L "$dependency" "$additions$target"
            owner=$(dpkg-query --search "$dependency" 2>/dev/null || dpkg-query --search "$(readlink -f "$dependency")")
            printf '%s\n' "$owner" | sed -n '1s/: \/.*//p' >> "$packages"
        fi
    done
done
cp -a /usr/share/fonts/truetype/dejavu "$additions/usr/share/fonts/truetype/"
printf '%s\n' fonts-dejavu-core >> "$packages"
sh docker/runtime/write-package-status.sh "$runtime_root" "$additions" "$packages"
sort -u "$packages" | while IFS= read -r package; do
    copyright="/usr/share/doc/${package%%:*}/copyright"
    if [ -f "$copyright" ]; then
        mkdir -p "$additions$(dirname "$copyright")"
        cp -L "$copyright" "$additions$copyright"
    fi
done
cc -O2 -Wall -Wextra -Werror -fPIE -pie -Wl,-z,relro,-z,now \
    docker/runtime/health-probe.c -o "$additions/app/honua-healthcheck"
cc -O2 -Wall -Wextra -Werror -fPIE -pie -Wl,-z,relro,-z,now \
    docker/runtime/secret-entrypoint.c -o "$additions/app/honua-entrypoint"
mkdir -p "$additions/tmp/honua-logs" "$additions/tmp/honua-cache" \
    "$additions/tmp/dotnet-diagnostics" "$additions/tmp/honua-temp" \
    "$additions/tmp/honua-storage" "$additions/var/lib/honua/storage"
chmod 1777 "$additions/tmp"
find "$additions/tmp" -mindepth 1 -type d -exec chmod 750 {} +
chmod 750 "$additions/var/lib/honua/storage"
find "$additions/tmp" -mindepth 1 -type d -exec chown 1001:1001 {} +
chown -R 1001:1001 "$additions/var/lib/honua"
cp -a "$additions/." "$runtime_root/"
