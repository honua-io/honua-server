#!/usr/bin/env python3
"""Deduplicate a capture in place: shapes and replayable requests are stored once (#3558).

runs/<name>/exchanges.jsonl keeps one line per exchange with fingerprints only;
runs/<name>/shapes.json maps shape_sha256 -> redacted shape XML, and
runs/<name>/requests.json maps replay_sha256 -> verbatim replayable request.
Idempotent.
"""
import hashlib
import json
import sys
from pathlib import Path


def compact(run: Path) -> None:
    shapes_path, requests_path = run / "shapes.json", run / "requests.json"
    shapes = json.loads(shapes_path.read_text(encoding="utf-8")) if shapes_path.exists() else {}
    requests = json.loads(requests_path.read_text(encoding="utf-8")) if requests_path.exists() else {}
    lines = []
    for line in (run / "exchanges.jsonl").read_text(encoding="utf-8").splitlines():
        entry = json.loads(line)
        for side in ("request", "response"):
            part = entry.get(side)
            if part and "shape_xml" in part:
                shapes[part["shape_sha256"]] = part.pop("shape_xml")
        request = entry.get("request")
        if request and "replay_xml" in request:
            xml = request.pop("replay_xml")
            if xml is not None:
                digest = hashlib.sha256(xml.encode("utf-8")).hexdigest()
                requests[digest] = xml
                request["replay_sha256"] = digest
            else:
                request["replay_sha256"] = None
        lines.append(json.dumps(entry, sort_keys=True))
    (run / "exchanges.jsonl").write_text("\n".join(lines) + "\n", encoding="utf-8")
    shapes_path.write_text(json.dumps(shapes, indent=1, sort_keys=True) + "\n", encoding="utf-8")
    requests_path.write_text(json.dumps(requests, indent=1, sort_keys=True) + "\n", encoding="utf-8")


if __name__ == "__main__":
    for path in sys.argv[1:]:
        compact(Path(path))
