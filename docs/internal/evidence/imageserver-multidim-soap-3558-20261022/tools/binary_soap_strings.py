"""Structural view of an Esri binary SOAP message (#3558).

ArcGIS Server answers GetMultidimensionalInfo only in its binary encoding
(the XML binding faults). The binary format is undocumented, but element,
key and string values are UTF-16LE runs, so their order is a usable shape
fingerprint. Numbers are not decoded; only printable strings of length >= 2.
"""
import hashlib
import re
import sys
from pathlib import Path

UTF16_RUN = re.compile(rb"(?:[\x20-\x7e]\x00){2,}")


def strings(payload: bytes) -> list[str]:
    return [match.group().decode("utf-16-le") for match in UTF16_RUN.finditer(payload)]


def fingerprint(payload: bytes) -> dict:
    found = strings(payload)
    return {"strings": found, "strings_sha256": hashlib.sha256("\n".join(found).encode()).hexdigest()}


if __name__ == "__main__":
    for path in sys.argv[1:]:
        print("\n".join(strings(Path(path).read_bytes())))
