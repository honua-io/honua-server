#!/usr/bin/env python3
"""Write manifest.json: SHA-256 of every file in this bundle as staged in git (#3558)."""
import hashlib
import json
import subprocess
from pathlib import Path

BUNDLE = Path(__file__).resolve().parent.parent


def main():
    names = subprocess.run(["git", "ls-files", "--", "."], cwd=BUNDLE, check=True,
                           capture_output=True, text=True).stdout.split()
    files = {}
    for name in sorted(names):
        if name == "manifest.json":
            continue
        blob = subprocess.run(["git", "show", f":./{name}"], cwd=BUNDLE, check=True, capture_output=True).stdout
        files[name] = {"sha256": hashlib.sha256(blob).hexdigest(), "bytes": len(blob)}
    (BUNDLE / "manifest.json").write_text(json.dumps({"files": files}, indent=1, sort_keys=True) + "\n",
                                          encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
