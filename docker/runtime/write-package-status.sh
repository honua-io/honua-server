#!/bin/sh
set -eu
runtime_root=$1
additions=$2
packages=$3
records=$(mktemp)
trap 'rm -f "$records"' EXIT
sort -u "$packages" | while IFS= read -r package; do
    installed=$(dpkg-query --show --showformat='${Version}' "$package")
    candidate=$(apt-cache policy "$package" | awk '$1 == "Candidate:" { print $2; exit }')
    test -n "$candidate"
    dpkg --compare-versions "$installed" ge "$candidate"
    dpkg-query --status "$package"
    printf '\n'
done > "$records"
mkdir -p "$additions/var/lib/dpkg"
awk '
    BEGIN { RS = ""; ORS = "\n\n" }
    {
        package = ""
        count = split($0, lines, "\n")
        for (line = 1; line <= count; line++) {
            if (lines[line] ~ /^Package: /) package = substr(lines[line], 10)
        }
        if (package == "") exit 1
        if (!(package in records)) order[++packages] = package
        records[package] = $0
    }
    END { for (entry = 1; entry <= packages; entry++) print records[order[entry]] }
' "$runtime_root/var/lib/dpkg/status" "$records" > "$additions/var/lib/dpkg/status"
